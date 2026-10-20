using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase05BackpressureTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task FullQueue_PreservesDuplicateAndSequence_AndRejectsOnlyNewEntry()
    {
        var c = new Phase03Context(fixture, configure: o => o.MaxWaitingUsers = 2);
        var first = await c.Queue.EnqueueAsync("first");
        await c.Queue.EnqueueAsync("second");
        var sequence = await c.Db.StringGetAsync(c.Options.QueueSequenceKey);
        var score = await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "first");
        Assert.Equal(first.Position, (await c.Queue.EnqueueAsync("first")).Position);
        await Assert.ThrowsAsync<WaitingQueueFullException>(() => c.Queue.EnqueueAsync("rejected"));
        Assert.Equal(sequence, await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        Assert.Equal(score, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "first"));
        Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "rejected"));
        Assert.Equal(new[] { "first", "second" }, (await c.Db.SortedSetRangeByRankAsync(c.Options.QueueKey)).Select(x => x.ToString()));
    }

    [Fact]
    public async Task ConcurrentInstances_CannotExceedLimit_DuringBurst()
    {
        var c = new Phase03Context(fixture, configure: o => o.MaxWaitingUsers = 17);
        var other = new Gatekeeper.Infrastructure.Services.RedisWaitingQueue(fixture.Connection,
            Microsoft.Extensions.Options.Options.Create(c.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisWaitingQueue>.Instance);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 128).Select(async i =>
        {
            try { await (i % 2 == 0 ? c.Queue : other).EnqueueAsync($"user-{i}"); return true; }
            catch (WaitingQueueFullException) { return false; }
        }));
        Assert.Equal(17, attempts.Count(x => x));
        Assert.Equal(17, await c.Queue.GetQueueLengthAsync());
        Assert.Equal("17", (await c.Db.StringGetAsync(c.Options.QueueSequenceKey)).ToString());
        var scores = await c.Db.SortedSetRangeByRankWithScoresAsync(c.Options.QueueKey);
        Assert.Equal(Enumerable.Range(1, 17).Select(x => (double)x), scores.Select(x => x.Score));
    }

    [Fact]
    public async Task Recovery_RestoresAcceptedUserAboveLimit_WithOriginalScore()
    {
        var c = new Phase03Context(fixture, configure: o => o.MaxWaitingUsers = 1);
        var claim = await c.ClaimAsync("original");
        await c.Queue.EnqueueAsync("new");
        claim = await c.ExpireLeaseAsync(claim);
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(claim));
        Assert.Equal(2, await c.Queue.GetQueueLengthAsync());
        Assert.Equal((double)claim.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "original"));
        Assert.Equal(1, (await c.Queue.EnqueueAsync("original")).Position);
        await Assert.ThrowsAsync<WaitingQueueFullException>(() => c.Queue.EnqueueAsync("blocked"));
        var next = (await c.Store.ClaimNextAsync(AdmissionId.New(), 10, c.Options.ProcessingLease)).Claim!;
        Assert.Equal("original", next.UserId);
    }

    [Fact]
    public async Task ProcessingAndActiveRejections_PrecedeQueueFull()
    {
        var c = new Phase03Context(fixture, configure: o => o.MaxWaitingUsers = 1);
        var claim = await c.ClaimAsync("processing");
        await c.Queue.EnqueueAsync("waiting");
        var processing = await Assert.ThrowsAsync<AdmissionAlreadyProcessingException>(() => c.Queue.EnqueueAsync("processing"));
        Assert.Contains("Processing", processing.Message);
        await c.CommitAsync(claim);
        var active = await Assert.ThrowsAsync<AdmissionAlreadyActiveException>(() => c.Queue.EnqueueAsync("processing"));
        Assert.Contains("active", active.Message);
        await Assert.ThrowsAsync<WaitingQueueFullException>(() => c.Queue.EnqueueAsync("new"));
    }

    [Fact]
    public async Task DisabledLimit_PreservesUnlimitedAcceptance_AndRedisErrorsRemainErrors()
    {
        var c = new Phase03Context(fixture);
        Assert.Equal(0, c.Options.MaxWaitingUsers);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => c.Queue.EnqueueAsync($"user-{i}")));
        Assert.Equal(100, await c.Queue.GetQueueLengthAsync());
        var broken = new Phase03Context(fixture, configure: o => o.MaxWaitingUsers = 1);
        await broken.Db.ListRightPushAsync(broken.Options.QueueKey, "wrong-type");
        await Assert.ThrowsAsync<RedisServerException>(() => broken.Queue.EnqueueAsync("user"));
    }

    [Fact]
    public async Task ServerOutage_IsNotReportedAsQueueFull()
    {
        await using var container = new RedisBuilder("redis:7.0").Build();
        await container.StartAsync();
        var config = ConfigurationOptions.Parse(container.GetConnectionString());
        config.AsyncTimeout = 500; config.BacklogPolicy = BacklogPolicy.FailFast;
        using var redis = await ConnectionMultiplexer.ConnectAsync(config);
        var c = new Phase03Context(redis, configure: o => o.MaxWaitingUsers = 1);
        await c.Queue.EnqueueAsync("already-full");
        await container.StopAsync();
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Queue.EnqueueAsync("new"));
    }
}
