using Gatekeeper.Domain.Enums;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase04DeliveryTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData("publish-failure")]
    [InlineData("offline-subscriber")]
    [InlineData("crash-before-publish")]
    [InlineData("lost-commit-response")]
    public async Task CommittedPass_RemainsDiscoverableAcrossDeliveryFailures(string failure)
    {
        var c = new Phase03Context(fixture, capacity: 1, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var published = 0;
        var faults = new FaultingClaimStore(c.Store);
        using var shutdown = new CancellationTokenSource();
        if (failure == "lost-commit-response") faults.LostCommitResponses = 1;
        if (failure == "crash-before-publish") faults.AfterCommit = shutdown.Cancel;
        var real = new RedisAdmissionNotifier(fixture.Connection, Options.Create(c.Options));
        var notifier = new CallbackNotifier(async user =>
        {
            published++;
            Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync(user));
            Assert.NotNull(await c.Store.GetPassAsync(user));
            if (failure == "publish-failure") throw new TimeoutException("Simulated Publish failure.");
            await real.NotifyReadyAsync(user); // No subscriber is connected.
        });
        var worker = c.Worker(notifier, faults);
        if (failure == "crash-before-publish")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(shutdown.Token));
        else if (failure == "lost-commit-response")
            await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        else await worker.ProcessBatchAsync();
        var token = await c.Store.GetPassAsync("user");
        Assert.NotNull(token);
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        if (failure == "lost-commit-response")
        {
            Assert.Equal(0, published);
            await worker.ProcessBatchAsync();
            Assert.Equal(1, published);
            Assert.Single(faults.ClaimIds);
            Assert.Single(faults.CommitTokens);
        }
        else
        {
            // A new worker has no process-local history. Committed state is sufficient.
            await c.Worker(notifier).ProcessBatchAsync();
            Assert.Equal(failure == "crash-before-publish" ? 0 : 1, published);
        }
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task ConnectedAndDuplicateReadySignals_OnlyWakeReaders_AndNeverExposeToken()
    {
        var c = new Phase03Context(fixture);
        var channel = RedisChannel.Literal(c.Options.BuildNotifyChannel("user"));
        var subscriber = fixture.Connection.GetSubscriber();
        var queue = await subscriber.SubscribeAsync(channel);
        try
        {
            var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
            var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey());
            var notifier = new RedisAdmissionNotifier(fixture.Connection, Options.Create(c.Options));
            for (int i = 0; i < 3; i++)
            {
                await notifier.NotifyReadyAsync("user");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var message = await queue.ReadAsync(timeout.Token);
                Assert.Equal("ready", message.Message.ToString());
                Assert.DoesNotContain(token, message.Message.ToString());
                Assert.DoesNotContain(claim.AdmissionId.ToString(), message.Message.ToString());
                Assert.Equal(token, await c.Store.GetPassAsync("user"));
            }
            Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
            Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        }
        finally { await subscriber.UnsubscribeAsync(channel); }
    }

    [Fact]
    public async Task DelayedReadyAfterExpiry_CannotRetrieveExpiredPass()
    {
        var c = new Phase03Context(fixture);
        await Phase04PollingTests.ActivateAsync(c, seconds: 2);
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.TokenKey());
        var subscriber = fixture.Connection.GetSubscriber();
        var channel = RedisChannel.Literal(c.Options.BuildNotifyChannel("user"));
        var messages = await subscriber.SubscribeAsync(channel);
        try
        {
            await new RedisAdmissionNotifier(fixture.Connection, Options.Create(c.Options)).NotifyReadyAsync("user");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("ready", (await messages.ReadAsync(timeout.Token)).Message.ToString());
            Assert.Equal(AdmissionStatus.NotFound, await c.Store.GetStatusAsync("user"));
            Assert.Null(await c.Store.GetPassAsync("user"));
        }
        finally { await subscriber.UnsubscribeAsync(channel); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashBeforeCommit_NeverExposesSignedPass_AndRecoveryPreservesFifo(bool generate)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        if (generate)
        {
            var now = DateTime.UtcNow;
            Assert.NotEmpty(c.Tokens.GenerateToken(new(claim.AdmissionId, "user", now, now.AddMinutes(5))));
        }
        Assert.Null(await c.Store.GetPassAsync("user"));
        claim = await c.ExpireLeaseAsync(claim); // Simulate crash by abandoning the worker reservation.
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(claim));
        Assert.Equal((double)claim.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user"));
        await c.Worker(new CallbackNotifier(_ => Task.CompletedTask)).ProcessBatchAsync();
        var token = await c.Store.GetPassAsync("user");
        Assert.NotNull(token);
        Assert.NotEqual(claim.AdmissionId, (await c.Tokens.ValidateTokenAsync(token))!.AdmissionId);
    }
}
