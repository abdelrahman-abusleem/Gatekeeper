using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class BackpressureTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task FullQueue_PreservesDuplicateAndSequence_AndRejectsOnlyNewEntry()
    {
        var c = new AdmissionTestContext(fixture, configure: o => o.MaxWaitingUsers = 2);
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
        var c = new AdmissionTestContext(fixture, configure: o => o.MaxWaitingUsers = 17);
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
        var c = new AdmissionTestContext(fixture, configure: o => o.MaxWaitingUsers = 1);
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
        var c = new AdmissionTestContext(fixture, configure: o => o.MaxWaitingUsers = 1);
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
        var c = new AdmissionTestContext(fixture);
        Assert.Equal(0, c.Options.MaxWaitingUsers);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => c.Queue.EnqueueAsync($"user-{i}")));
        Assert.Equal(100, await c.Queue.GetQueueLengthAsync());
        var broken = new AdmissionTestContext(fixture, configure: o => o.MaxWaitingUsers = 1);
        await broken.Db.ListRightPushAsync(broken.Options.QueueKey, "wrong-type");
        await Assert.ThrowsAsync<RedisServerException>(() => broken.Queue.EnqueueAsync("user"));
    }

    [Fact]
    public async Task ServerOutage_IsNotReportedAsQueueFull()
    {
        await using var container = new RedisBuilder(TestRedisImage.Name).Build();
        await container.StartAsync();
        var config = ConfigurationOptions.Parse(container.GetConnectionString());
        config.AsyncTimeout = 500; config.BacklogPolicy = BacklogPolicy.FailFast;
        using var redis = await ConnectionMultiplexer.ConnectAsync(config);
        var c = new AdmissionTestContext(redis, configure: o => o.MaxWaitingUsers = 1);
        await c.Queue.EnqueueAsync("already-full");
        await container.StopAsync();
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Queue.EnqueueAsync("new"));
    }
}


public sealed class SharedCapacityTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task RegisteredLowerCapacity_BoundsConcurrentMixedCallers()
    {
        var c = new AdmissionTestContext(fixture, capacity: 5);
        await c.ClaimAsync("reserved");
        await EnqueueUsersAsync(c);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            await ClaimAsync(c, i % 2 == 0 ? 5 : 10);
            var counts = await c.CountsAsync();
            Assert.InRange(counts.Active + counts.Processing, 1, 5);
        }));
        Assert.Equal((46L, 5L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task LoweringCapacity_PreservesReservationsAndBlocksUntilTheyDrain()
    {
        var c = new AdmissionTestContext(fixture);
        var claims = new List<AdmissionClaim>();
        for (int i = 0; i < 6; i++) claims.Add(await c.ClaimAsync("held-" + i));
        await c.Queue.EnqueueAsync("next");
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await ClaimAsync(c, 5)).Status);
        Assert.Equal("5", (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await ClaimAsync(c, 10)).Status);
        Assert.Equal((1L, 6L, 0L), await c.CountsAsync());
        foreach (var claim in claims)
        {
            await c.CommitAsync(claim);
            var counts = await c.CountsAsync();
            Assert.Equal(6, counts.Active + counts.Processing);
        }
        foreach (var claim in claims.Take(2))
            Assert.Equal(AdmissionReleaseStatus.Released, await c.Store.ReleaseAsync(claim.UserId, claim.AdmissionId));
        Assert.Equal((1L, 0L, 4L), await c.CountsAsync());
        Assert.Equal(AdmissionClaimStatus.Claimed, (await ClaimAsync(c, 10)).Status);
        Assert.Equal((0L, 1L, 4L), await c.CountsAsync());
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await ClaimAsync(c, 10)).Status);
    }

    [Fact]
    public async Task AbsentRegistration_BoundsConcurrentCallersAtTheirOwnCapacity()
    {
        var c = new AdmissionTestContext(fixture);
        await EnqueueUsersAsync(c);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            await ClaimAsync(c, 10);
            var counts = await c.CountsAsync();
            Assert.InRange(counts.Active + counts.Processing, 0, 10);
        }));
        Assert.Equal((40L, 10L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task EqualCapacity_RefreshesTtlAndHigherCapacityCannotRefreshOrRaiseIt()
    {
        var c = new AdmissionTestContext(fixture);
        await ClaimAsync(c, 5);
        await c.Db.KeyExpireAsync(c.Options.CapacityKey, TimeSpan.FromSeconds(10));
        await ClaimAsync(c, 10);
        Assert.Equal("5", (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.InRange((long)await c.Db.ExecuteAsync("PTTL", c.Options.CapacityKey), 1, 10000);
        await ClaimAsync(c, 5);
        Assert.InRange((long)await c.Db.ExecuteAsync("PTTL", c.Options.CapacityKey), 30000, 32000);
        await ClaimAsync(c, 3);
        Assert.Equal("3", (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.InRange((long)await c.Db.ExecuteAsync("PTTL", c.Options.CapacityKey), 30000, 32000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRegistration_AllowsHigherCapacityToRegister(bool expire)
    {
        var c = new AdmissionTestContext(fixture);
        await ClaimAsync(c, 5);
        if (expire)
        {
            await c.Db.ExecuteAsync("PEXPIRE", c.Options.CapacityKey, 1);
            await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.Options.CapacityKey);
        }
        else await c.Db.KeyDeleteAsync(c.Options.CapacityKey);
        await EnqueueUsersAsync(c);
        for (int i = 0; i < 10; i++) Assert.Equal(AdmissionClaimStatus.Claimed, (await ClaimAsync(c, 10)).Status);
        Assert.Equal("10", (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await ClaimAsync(c, 10)).Status);
    }

    [Fact]
    public async Task ExistingReservationRetry_DoesNotRejudgeOrRefreshCapacity()
    {
        var c = new AdmissionTestContext(fixture);
        var claim = await c.ClaimAsync("first");
        await c.ClaimAsync("second");
        await c.Db.StringSetAsync(c.Options.CapacityKey, "1", TimeSpan.FromSeconds(10));
        Assert.Equal(claim, (await c.Store.ClaimNextAsync(claim.AdmissionId, 10, c.Options.ProcessingLease)).Claim);
        Assert.InRange((long)await c.Db.ExecuteAsync("PTTL", c.Options.CapacityKey), 1, 10000);
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await ClaimAsync(c, 10)).Status);
        Assert.Equal((0L, 2L, 0L), await c.CountsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("invalid")]
    [InlineData("2147483648")]
    public async Task MalformedRegistration_FailsBeforeAnyAdmissionWrite(string record)
    {
        var c = new AdmissionTestContext(fixture);
        await c.Queue.EnqueueAsync("waiting");
        await c.Db.StringSetAsync(c.Options.CapacityKey, record);
        var id = AdmissionId.New();
        var error = await Assert.ThrowsAsync<RedisServerException>(() => c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease));
        Assert.Contains("Invalid capacity record", error.Message);
        Assert.Equal(record, (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        Assert.False(await c.Db.KeyExistsAsync(c.Options.AdmissionRecordsKey));
        Assert.False(await c.Db.KeyExistsAsync(c.Options.ClaimAttemptKeyPrefix + id));
    }

    [Fact]
    public async Task WrongRegistrationType_FailsBeforeAnyAdmissionWrite()
    {
        var c = new AdmissionTestContext(fixture);
        await c.Queue.EnqueueAsync("waiting");
        await c.Db.ListRightPushAsync(c.Options.CapacityKey, "invalid");
        var error = await Assert.ThrowsAsync<RedisServerException>(() => ClaimAsync(c, 10));
        Assert.Contains("Inconsistent claim key type", error.Message);
        Assert.Equal("invalid", (await c.Db.ListGetByIndexAsync(c.Options.CapacityKey, 0)).ToString());
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        Assert.False(await c.Db.KeyExistsAsync(c.Options.AdmissionRecordsKey));
    }

    [Fact]
    public async Task InvalidWaitingScore_ChangesOnlyTheCapacityRegistration()
    {
        var c = new AdmissionTestContext(fixture);
        await c.Db.SortedSetAddAsync(c.Options.QueueKey, "waiting", 0);
        var error = await Assert.ThrowsAsync<RedisServerException>(() => ClaimAsync(c, 5));
        Assert.Contains("Invalid waiting sequence score", error.Message);
        Assert.Equal("5", (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.Equal(0d, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "waiting"));
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        Assert.False(await c.Db.KeyExistsAsync(c.Options.AdmissionRecordsKey));
    }

    [Theory]
    [InlineData("999")]
    [InlineData("259200001")]
    [InlineData("1000.5")]
    [InlineData("invalid")]
    public async Task InvalidRegistrationTtl_IsRejectedBeforeWriting(string ttl)
    {
        var c = new AdmissionTestContext(fixture);
        await c.Queue.EnqueueAsync("waiting");
        var id = AdmissionId.New();
        var error = await Assert.ThrowsAsync<RedisServerException>(() => c.Db.ScriptEvaluateAsync(
            RedisScriptSource.Load("ClaimNextUser.lua").Value,
            new RedisKeys(c.Options).ForClaim(id),
            [id.ToString(), 5, 30000, 300000, 60000, ttl]));
        Assert.Contains("Invalid capacity record TTL", error.Message);
        Assert.False(await c.Db.KeyExistsAsync(c.Options.CapacityKey));
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("other:{other}:capacity")]
    [InlineData("gatekeeper:{admission}:waiting")]
    [InlineData("gatekeeper:{admission}:pass:overlap")]
    public void InvalidCapacityKey_IsRejected(string key)
    {
        var options = new QueueOptions { CapacityKey = key };
        Assert.False(QueueOptionsExtensions.HasValidRedisKeys(options));
    }

    [Fact]
    public void RegistrationTtl_UsesTwoReleaseIntervalsAndOneLease()
    {
        var options = new QueueOptions();
        Assert.Equal(150000, QueueOptionsExtensions.CapacityRecordTtlMilliseconds(options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeAttemptExpiry_DoesNotExpireTheLowerCapacityRegistration(bool waitForAttemptExpiry)
    {
        var c = new AdmissionTestContext(fixture, capacity: 2, configure: UuidRetentionTestSupport.ShortPolicy);
        await c.ClaimAsync("held");
        var id = AdmissionId.New();
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease)).Status);
        await c.Queue.EnqueueAsync("later");
        if (waitForAttemptExpiry)
            await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.Options.ClaimAttemptKeyPrefix + id);
        Assert.Equal("1", (await c.Db.StringGetAsync(c.Options.CapacityKey)).ToString());
        Assert.Equal(AdmissionClaimStatus.CapacityFull, (await ClaimAsync(c, 2)).Status);
        Assert.Equal((1L, 1L, 0L), await c.CountsAsync());
    }

    private static Task EnqueueUsersAsync(AdmissionTestContext c) =>
        Task.WhenAll(Enumerable.Range(0, 50).Select(i => c.Queue.EnqueueAsync("waiting-" + i)));

    private static Task<AdmissionClaimResult> ClaimAsync(AdmissionTestContext c, int capacity) =>
        c.Store.ClaimNextAsync(AdmissionId.New(), capacity, c.Options.ProcessingLease);
}
