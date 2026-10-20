using System.Text.Json.Nodes;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase06ReleaseTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_IsAtomicRepeatSafe_AndPreservesExactAttemptDeadline(bool legacyPersistent)
    {
        var c = new Phase03Context(fixture, capacity: 1);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        if (legacyPersistent) await c.Db.KeyPersistAsync(c.AttemptKey(claim));
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim));
        Assert.Equal(AdmissionReleaseStatus.Released, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        var record = JsonNode.Parse((await c.Db.StringGetAsync(c.AttemptKey(claim))).ToString())!;
        Assert.Equal("COMMITTED", record["status"]!.GetValue<string>());
        Assert.True(record["released"]!.GetValue<bool>());
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, token));
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.Equal((0L, 0L, 0L), await c.CountsAsync());
        Assert.Equal("next", (await c.ClaimAsync("next")).UserId);
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(claim));
        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(claim.AdmissionId, 1, c.Options.ProcessingLease));
    }

    [Fact]
    public async Task LegacyV4Release_KeepsPersistentAttempt_AndReconciles()
    {
        var c = new Phase03Context(fixture);
        var claim = await UuidRetentionTestSupport.SeedRecordedAsync(c, new AdmissionId(Guid.NewGuid()));
        var expiry = await c.CommitAsync(claim);
        await c.Db.KeyPersistAsync(c.AttemptKey(claim));
        Assert.Equal(AdmissionReleaseStatus.Released, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, "test-token"));
    }

    [Fact]
    public async Task StaleReleaseAndOldToken_CannotAffectNewAdmission()
    {
        var c = new Phase03Context(fixture);
        var (old, oldToken, _) = await Phase04PollingTests.ActivateAsync(c);
        await c.Store.ReleaseAsync("user", old.AdmissionId);
        var (current, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey());
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("user", old.AdmissionId));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(old, oldToken));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(oldToken, "user"));
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
        Assert.Equal(current.AdmissionId.ToString(), (await c.Db.StringGetAsync(c.IdentityKey())).ToString());
    }

    [Fact]
    public async Task WrongUserAndProcessingRelease_LeaveReservationsUntouched()
    {
        var c = new Phase03Context(fixture);
        var (active, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var processing = await c.ClaimAsync("processing");
        var attempt = await c.Db.StringGetAsync(c.AttemptKey(processing));
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("other", active.AdmissionId));
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("processing", processing.AdmissionId));
        Assert.Equal(attempt, await c.Db.StringGetAsync(c.AttemptKey(processing)));
        Assert.Equal((0L, 1L, 1L), await c.CountsAsync());
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
    }

    [Theory]
    [InlineData("attempt-missing")]
    [InlineData("attempt-malformed")]
    [InlineData("marker")]
    [InlineData("claimed")]
    [InlineData("identity-missing")]
    [InlineData("token-persistent")]
    public async Task Corruption_IsDeniedBeforeAnyReleaseWrite(string fault)
    {
        var c = new Phase03Context(fixture);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var key = c.AttemptKey(claim);
        if (fault == "attempt-missing") await c.Db.KeyDeleteAsync(key);
        if (fault == "attempt-malformed") await c.Db.StringSetAsync(key, "malformed");
        if (fault is "marker" or "claimed")
        {
            var record = JsonNode.Parse((await c.Db.StringGetAsync(key)).ToString())!;
            if (fault == "marker") record["released"] = "true";
            else record["status"] = null;
            await Phase04PollingTests.SetKeepingExpiryAsync(c, key, record.ToJsonString());
        }
        if (fault == "identity-missing") await c.Db.KeyDeleteAsync(c.IdentityKey());
        if (fault == "token-persistent") await c.Db.KeyPersistAsync(c.TokenKey());
        var before = await c.Db.StringGetAsync(key);
        Assert.Equal(AdmissionReleaseStatus.Inconsistent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(before, await c.Db.StringGetAsync(key));
        Assert.Equal(token, (await c.Db.StringGetAsync(c.TokenKey())).ToString());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task ReleasedMarkerWithReappearedSameIdentity_IsInconsistent()
    {
        var c = new Phase03Context(fixture);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var record = JsonNode.Parse((await c.Db.StringGetAsync(c.AttemptKey(claim))).ToString())!;
        record["released"] = true;
        await Phase04PollingTests.SetKeepingExpiryAsync(c, c.AttemptKey(claim), record.ToJsonString());
        Assert.Equal(AdmissionCommitStatus.Inconsistent, await c.Store.GetCommitStatusAsync(claim, token));
        Assert.Equal(AdmissionReleaseStatus.Inconsistent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
    }

    [Fact]
    public async Task ReleaseBetweenLostCommitResponseAndReconciliation_DoesNotNotifyOrRecommit()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var faults = new ObservedClaimStore(c.Store)
        {
            AfterCommit = (_, _) => Task.FromException(new TimeoutException("Simulated lost Commit response."))
        };
        var notifications = 0;
        using var worker = c.Worker(new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; }), faults);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        var commit = Assert.Single(faults.Commits);
        await c.Store.ReleaseAsync("user", commit.Claim.AdmissionId);
        await worker.ProcessBatchAsync();
        Assert.Single(faults.Commits);
        Assert.Equal(0, notifications);
        Assert.Equal((0L, 0L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task LostReleaseResponse_ReevaluatesOriginalConditionWithoutFalseHistoricalSuccess()
    {
        var c = new Phase03Context(fixture);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await c.Store.ReleaseAsync("user", claim.AdmissionId);
            throw new TimeoutException("Injected lost Release response after real execution.");
        });
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, token));
    }

    [Fact]
    public async Task ConcurrentReleaseValidationAndClaim_PreserveCapacity_AndDenyAfterRelease()
    {
        var c = new Phase03Context(fixture, capacity: 1);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        await c.Queue.EnqueueAsync("next");
        var releases = Task.WhenAll(Enumerable.Range(0, 16).Select(_ => c.Store.ReleaseAsync("user", claim.AdmissionId)));
        var validations = Task.WhenAll(Enumerable.Range(0, 32).Select(_ => c.Store.ValidateAdmissionAsync(token, "user")));
        var next = c.Store.ClaimNextAsync(AdmissionId.New(), 1, c.Options.ProcessingLease);
        var releaseResults = await releases;
        Assert.Equal(1, releaseResults.Count(x => x == AdmissionReleaseStatus.Released));
        Assert.All(await validations, x => Assert.Contains(x, new[] { AdmissionValidationStatus.Valid, AdmissionValidationStatus.Inactive }));
        await next;
        var counts = await c.CountsAsync();
        Assert.InRange(counts.Active + counts.Processing, 0, 1);
        for (int i = 0; i < 8; i++)
            Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
    }

    [Fact]
    public async Task NaturalExpiry_IsNotCurrent_AndDoesNotTouchAttemptRetention()
    {
        var c = new Phase03Context(fixture);
        var (claim, _, _) = await Phase04PollingTests.ActivateAsync(c, seconds: 2);
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim));
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.TokenKey());
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task WrongTypes_RejectBeforeMutation(int keyIndex)
    {
        var c = new Phase03Context(fixture);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        string[] keys = [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey,
            c.Options.ActivePassesKey, c.TokenKey(), c.IdentityKey(), c.AttemptKey(claim)];
        await c.Db.KeyDeleteAsync(keys[keyIndex]);
        await c.Db.ListRightPushAsync(keys[keyIndex], "wrong-type");
        Assert.Equal(AdmissionReleaseStatus.Inconsistent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal("wrong-type", (await c.Db.ListGetByIndexAsync(keys[keyIndex], 0)).ToString());
        if (keyIndex != 4) Assert.Equal(token, (await c.Db.StringGetAsync(c.TokenKey())).ToString());
    }

    [Fact]
    public async Task CancellationAfterReleaseDispatch_DoesNotUndoRemoteReleaseOrInventSuccess()
    {
        var c = new Phase03Context(fixture);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim));
        using var canceled = new CancellationTokenSource();
        await c.Db.ExecuteAsync("CLIENT", "PAUSE", "2000", "ALL");
        Task<AdmissionReleaseStatus> release;
        try
        {
            release = c.Store.ReleaseAsync("user", claim.AdmissionId, canceled.Token);
            canceled.Cancel();
        }
        finally { await c.Db.ExecuteAsync("CLIENT", "UNPAUSE"); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("user", claim.AdmissionId));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
    }

    [Fact]
    public async Task ReleasedAttemptReconciliation_LeavesNewerProcessingReservationUntouched()
    {
        var c = new Phase03Context(fixture);
        var (old, token, _) = await Phase04PollingTests.ActivateAsync(c);
        await c.Store.ReleaseAsync("user", old.AdmissionId);
        var current = await c.ClaimAsync();
        var before = await c.Db.StringGetAsync(c.AttemptKey(current));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(old, token));
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync("user", old.AdmissionId));
        Assert.Equal(before, await c.Db.StringGetAsync(c.AttemptKey(current)));
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PTTL", c.AttemptKey(current)));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
    }
}
