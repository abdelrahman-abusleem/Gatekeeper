using System.Text.Json.Nodes;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03RecoveryTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task Recovery_PreservesExactFifoAndAllowsFreshClaim_ButNeverOldCommit()
    {
        var c = new Phase03Context(fixture, capacity: 1);
        await c.Db.StringSetAsync(c.Options.QueueSequenceKey, "9007199254740989");
        var old = await c.ClaimAsync("first");
        Assert.Equal(9007199254740990, old.OriginalScore);
        await c.Queue.EnqueueAsync("second");
        var expired = await c.ExpireLeaseAsync(old);
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(expired));
        Assert.Equal(AdmissionRecoveryStatus.AlreadyResolved, await c.Store.RecoverAsync(expired));
        Assert.Equal((2L, 0L, 0L), await c.CountsAsync());
        Assert.Equal((double)old.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "first"));
        Assert.True((await c.Db.HashGetAsync(c.Options.AdmissionRecordsKey, "first")).IsNull);
        var attempt = JsonNode.Parse((await c.Db.StringGetAsync(c.AttemptKey(old))).ToString())!;
        Assert.Equal("RECOVERED", attempt["status"]!.GetValue<string>());
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(old));
        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(
            old.AdmissionId, 1, c.Options.ProcessingLease));
        var fresh = (await c.Store.ClaimNextAsync(AdmissionId.New(), 1, c.Options.ProcessingLease)).Claim!;
        Assert.Equal("first", fresh.UserId);
        Assert.Equal(old.OriginalScore, fresh.OriginalScore);
        Assert.NotEqual(old.AdmissionId, fresh.AdmissionId);
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(old));
        Assert.Equal(AdmissionRecoveryStatus.AlreadyResolved, await c.Store.RecoverAsync(expired));
        await c.CommitAsync(fresh);
        Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task MultipleRecoverers_ReleaseCapacityExactlyOnce()
    {
        var c = new Phase03Context(fixture, capacity: 1);
        var claim = await c.ExpireLeaseAsync(await c.ClaimAsync());
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => c.Store.RecoverAsync(claim)));
        Assert.Single(results, x => x == AdmissionRecoveryStatus.Recovered);
        Assert.Equal(15, results.Count(x => x == AdmissionRecoveryStatus.AlreadyResolved));
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        Assert.Empty(await c.Store.RecoverExpiredAsync(10));
        Assert.NotNull((await c.Store.ClaimNextAsync(AdmissionId.New(), 1, c.Options.ProcessingLease)).Claim);
    }

    [Fact]
    public async Task UnexpiredLease_IsNeverRecovered()
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        Assert.Equal(AdmissionRecoveryStatus.NotExpired, await c.Store.RecoverAsync(claim));
        Assert.Empty(await c.Store.RecoverExpiredAsync(10));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCommitAndRecovery_OnBothSidesOfDeadline_PreserveOneState(bool expired)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        if (expired) claim = await c.ExpireLeaseAsync(claim);
        async Task<bool> Commit()
        {
            try { await c.CommitAsync(claim); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var commit = Commit();
        var recover = c.Store.RecoverAsync(claim);
        await Task.WhenAll(commit, recover);
        Assert.Equal(!expired, await commit);
        Assert.Contains(await recover, expired
            ? new[] { AdmissionRecoveryStatus.Recovered }
            : new[] { AdmissionRecoveryStatus.NotExpired, AdmissionRecoveryStatus.AlreadyResolved });
        Assert.Equal(expired ? (1L, 0L, 0L) : (0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task BoundedScan_RecoversOnlyExpiredClaims_AndConcurrentScannersAreSafe()
    {
        var c = new Phase03Context(fixture);
        var a = await c.ExpireLeaseAsync(await c.ClaimAsync("a"));
        var b = await c.ExpireLeaseAsync(await c.ClaimAsync("b"));
        await c.ClaimAsync("current");
        var scans = await Task.WhenAll(c.Store.RecoverExpiredAsync(1), c.Store.RecoverExpiredAsync(1));
        Assert.All(scans, scan => Assert.InRange(scan.Count, 0, 1));
        await c.Store.RecoverExpiredAsync(10);
        Assert.Equal((2L, 1L, 0L), await c.CountsAsync());
        Assert.Equal((double)a.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "a"));
        Assert.Equal((double)b.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "b"));
    }

    [Theory]
    [InlineData("missing-record")]
    [InlineData("malformed-record")]
    [InlineData("scalar-record")]
    [InlineData("missing-attempt")]
    [InlineData("malformed-attempt")]
    [InlineData("missing-score")]
    [InlineData("different-user")]
    [InlineData("different-score")]
    [InlineData("different-lease")]
    [InlineData("active")]
    [InlineData("duplicate-waiting")]
    [InlineData("identity")]
    [InlineData("token")]
    public async Task CorruptMetadata_IsRetainedAndFailsClosed(string corruption)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ExpireLeaseAsync(await c.ClaimAsync());
        var raw = await c.Db.HashGetAsync(c.Options.AdmissionRecordsKey, "user");
        var record = JsonNode.Parse(raw.ToString())!;
        switch (corruption)
        {
            case "missing-record": await c.Db.HashDeleteAsync(c.Options.AdmissionRecordsKey, "user"); break;
            case "malformed-record": await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", "{bad"); break;
            case "scalar-record": await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", "42"); break;
            case "missing-attempt": await c.Db.KeyDeleteAsync(c.AttemptKey(claim)); break;
            case "malformed-attempt": await c.Db.StringSetAsync(c.AttemptKey(claim), "[]"); break;
            case "missing-score":
                record.AsObject().Remove("originalScore");
                await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", record.ToJsonString()); break;
            case "different-user":
                record["userId"] = "wrong-user";
                await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", record.ToJsonString()); break;
            case "different-score":
                record["originalScore"] = "999";
                await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", record.ToJsonString()); break;
            case "different-lease": await c.Db.SortedSetAddAsync(c.Options.ProcessingQueueKey, "user", 1); break;
            case "active": await c.Db.SortedSetAddAsync(c.Options.ActivePassesKey, "user", 1); break;
            case "duplicate-waiting": await c.Db.SortedSetAddAsync(c.Options.QueueKey, "user", 999); break;
            case "identity": await c.Db.StringSetAsync(c.IdentityKey(), "conflict"); break;
            case "token": await c.Db.StringSetAsync(c.TokenKey(), "conflict"); break;
        }
        var results = await c.Store.RecoverExpiredAsync(10);
        Assert.Equal(AdmissionRecoveryStatus.Inconsistent, Assert.Single(results).Status);
        Assert.NotNull(await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, "user"));
        if (corruption == "duplicate-waiting")
            Assert.Equal(999, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user"));
        else
            Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user"));
    }

    [Fact]
    public async Task StaleRecoverySnapshot_CannotOverwriteNewReservation()
    {
        var c = new Phase03Context(fixture);
        var old = await c.ExpireLeaseAsync(await c.ClaimAsync());
        await c.Store.RecoverAsync(old);
        var current = (await c.Store.ClaimNextAsync(AdmissionId.New(), 10, c.Options.ProcessingLease)).Claim!;
        Assert.Equal(AdmissionRecoveryStatus.AlreadyResolved, await c.Store.RecoverAsync(old));
        Assert.Equal(current, (await c.Store.ClaimNextAsync(current.AdmissionId, 10, c.Options.ProcessingLease)).Claim);
    }
}
