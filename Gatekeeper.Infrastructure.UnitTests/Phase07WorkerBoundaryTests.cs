using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase07WorkerBoundaryTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData("claim-dispatch")]
    [InlineData("claim-response")]
    [InlineData("signing")]
    [InlineData("prepared-token")]
    [InlineData("commit-dispatch")]
    [InlineData("commit-response")]
    [InlineData("notification")]
    public async Task CancellationAtLifecycleBoundary_RetainsAuthority_AndRestartPreservesFifo(string boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var c = new Phase03Context(fixture, batchSize: 1, capacity: 1);
        await c.Queue.EnqueueAsync("first");
        await c.Queue.EnqueueAsync("second");
        var observed = new ObservedClaimStore(c.Store);
        var notifications = 0;
        if (boundary == "claim-dispatch") observed.BeforeClaim = (_, ct) => CancelAsync(ct);
        if (boundary == "claim-response") observed.AfterClaim = (_, ct) => CancelAsync(ct);
        if (boundary == "commit-dispatch") observed.BeforeCommit = (_, ct) => CancelAsync(ct);
        if (boundary == "commit-response") observed.AfterCommit = (_, ct) => CancelAsync(ct);
        var signer = new BoundarySigner(c.Tokens, data =>
        {
            if (boundary == "signing") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            var result = c.Tokens.GenerateToken(data);
            if (boundary == "prepared-token") cancellation.Cancel();
            return result;
        });
        using (var worker = c.Worker(new CallbackNotifier(_ =>
        {
            notifications++;
            if (boundary == "notification") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        }), observed, signer))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(cancellation.Token, recover: false));
            Assert.Single(observed.Ids);
        }
        var committed = boundary is "commit-response" or "notification";
        var status = await c.Store.GetStatusAsync("first");
        Assert.Equal(committed ? AdmissionStatus.Active : boundary == "claim-dispatch" ? AdmissionStatus.Waiting : AdmissionStatus.Processing, status);
        Assert.Equal(2, (long)await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        if (status == AdmissionStatus.Processing)
        {
            var original = (await c.Store.ClaimNextAsync(observed.Ids.Single(), 1, c.Options.ProcessingLease)).Claim!;
            var expired = await c.ExpireLeaseAsync(original);
            Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(expired));
            Assert.Equal(1, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "first"));
        }
        var restartedNotifications = new List<string>();
        using var replacement = c.Worker(new CallbackNotifier(user => { restartedNotifications.Add(user); return Task.CompletedTask; }));
        await replacement.ProcessBatchAsync();
        Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("first"));
        Assert.NotNull(await c.Store.GetPassAsync("first"));
        Assert.Equal(2, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "second"));
        Assert.Equal(committed ? Array.Empty<string>() : ["first"], restartedNotifications);
        Assert.Equal(boundary == "notification" ? 1 : 0, notifications);

        Task CancelAsync(CancellationToken token)
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SigningFailureThenRetry_ReusesClaimAndTimestamp_WithoutAUserPenalty()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var generated = new List<AdmissionTokenData>();
        var observed = new ObservedClaimStore(c.Store);
        var notifications = 0;
        var signer = new BoundarySigner(c.Tokens, data =>
        {
            generated.Add(data);
            if (generated.Count == 1) throw new InvalidOperationException("Injected signer failure.");
            return c.Tokens.GenerateToken(data);
        });
        using var worker = c.Worker(new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; }), observed, signer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessBatchAsync(recover: false));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.Options.ClaimAttemptKeyPrefix + observed.Ids.Single()));
        await worker.ProcessBatchAsync(recover: false);
        Assert.Equal(generated[0], generated[1]);
        Assert.Single(observed.Ids.Distinct());
        Assert.Single(observed.Commits);
        Assert.Equal(1, notifications);
        Assert.Equal(1, (long)await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync((await c.Store.GetPassAsync("user"))!, "user"));
    }

    [Fact]
    public async Task CancellationAfterRecoveryExecution_DoesNotRepeatOrChangeOriginalScores()
    {
        var c = new Phase03Context(fixture, batchSize: 2);
        var first = await c.ExpireLeaseAsync(await c.ClaimAsync("first"));
        var second = await c.ExpireLeaseAsync(await c.ClaimAsync("second"));
        using var cancellation = new CancellationTokenSource();
        var observed = new ObservedClaimStore(c.Store)
        {
            AfterRecovery = _ => cancellation.Cancel()
        };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(cancellation.Token));
        Assert.Equal((2L, 0L, 0L), await c.CountsAsync());
        Assert.Equal(first.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "first"));
        Assert.Equal(second.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "second"));
        Assert.Empty(await c.Store.RecoverExpiredAsync(2));
        Assert.Equal(2, (long)await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
    }

    [Fact]
    public async Task SignedButUncommittedToken_RemainsInvalidAfterRecoveryAndNewCommit()
    {
        var c = new Phase03Context(fixture, capacity: 1);
        var old = await c.ClaimAsync();
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var oldToken = c.Tokens.GenerateToken(new(old.AdmissionId, "user", issued, issued.AddMinutes(5)));
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(oldToken));
        var expired = await c.ExpireLeaseAsync(old);
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(expired));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(oldToken, "user"));
        var fresh = (await c.Store.ClaimNextAsync(Gatekeeper.Domain.ValueObjects.AdmissionId.New(), 1, c.Options.ProcessingLease)).Claim!;
        var newToken = c.Tokens.GenerateToken(new(fresh.AdmissionId, "user", issued, issued.AddMinutes(5)));
        await c.Store.CommitAsync(fresh, newToken, issued.AddMinutes(5));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.Store.CommitAsync(old, oldToken, issued.AddMinutes(5)));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(oldToken, "user"));
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(newToken, "user"));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task IndependentRecoveryStores_RaceWithFreshClaims_WithoutCapacityOrFifoLoss()
    {
        var c = new Phase03Context(fixture, capacity: 2);
        var old = await c.ExpireLeaseAsync(await c.ClaimAsync("first"));
        await c.Queue.EnqueueAsync("second");
        var stores = Enumerable.Range(0, 4).Select(_ => new Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore(
            fixture.Connection, Microsoft.Extensions.Options.Options.Create(c.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore>.Instance,
            c.Tokens)).ToArray();
        var recoveries = stores.Select(store => store.RecoverAsync(old)).ToArray();
        var claims = stores.Select(store => store.ClaimNextAsync(Gatekeeper.Domain.ValueObjects.AdmissionId.New(), 2,
            c.Options.ProcessingLease)).ToArray();
        await Task.WhenAll(recoveries.Cast<Task>().Concat(claims));
        Assert.Single(await Task.WhenAll(recoveries), result => result == AdmissionRecoveryStatus.Recovered);
        var reserved = (await Task.WhenAll(claims)).Where(result => result.Claim is not null).Select(result => result.Claim!).ToArray();
        Assert.Equal(2, reserved.Length);
        Assert.Equal(2, reserved.Select(claim => claim.UserId).Distinct().Count());
        Assert.Equal(old.OriginalScore, Assert.Single(reserved, claim => claim.UserId == "first").OriginalScore);
        Assert.Equal((0L, 2L, 0L), await c.CountsAsync());
        Assert.Equal(2, (long)await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.Store.CommitAsync(old, "stale", DateTime.UtcNow.AddMinutes(5)));
    }

    private sealed class BoundarySigner(IAdmissionTokenService inner, Func<AdmissionTokenData, string> generate) : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => generate(data);
        public Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => inner.ValidateTokenAsync(token, cancellationToken);
    }
}
