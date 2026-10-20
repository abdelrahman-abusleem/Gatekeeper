using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

internal static class UuidRetentionTestSupport
{
    public static string ClaimScript => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "ClaimNextUser.lua"));
    public static async Task<long> NowAsync(IDatabase db)
    {
        var time = (RedisResult[])(await db.ExecuteAsync("TIME"))!;
        return long.Parse(time[0].ToString(), CultureInfo.InvariantCulture) * 1000
            + long.Parse(time[1].ToString(), CultureInfo.InvariantCulture) / 1000;
    }
    public static AdmissionId At(long milliseconds) => new(Guid.CreateVersion7(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)));
    public static void ShortPolicy(QueueOptions options)
    {
        options.ClaimIdAcceptanceWindowSeconds = 1;
        options.ClaimAttemptGraceSeconds = 1;
    }
    public static async Task WaitUntilMissingAsync(IDatabase db, RedisKey key)
    {
        var elapsed = Stopwatch.StartNew();
        while (await db.KeyExistsAsync(key))
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Expected Redis expiry did not occur.");
            await Task.Delay(20);
        }
    }
    public static async Task<AdmissionClaim> SeedRecordedAsync(Phase03Context c, AdmissionId id)
    {
        var current = await c.ClaimAsync();
        var attempt = JsonNode.Parse((await c.Db.StringGetAsync(c.AttemptKey(current))).ToString())!;
        attempt["admissionId"] = id.ToString();
        var record = JsonNode.Parse((await c.Db.HashGetAsync(c.Options.AdmissionRecordsKey, current.UserId)).ToString())!;
        record["admissionId"] = id.ToString();
        await c.Db.ScriptEvaluateAsync(
            "redis.call('SET',KEYS[1],ARGV[1]); redis.call('DEL',KEYS[2]); redis.call('HSET',KEYS[3],ARGV[2],ARGV[3]); return 1",
            [c.Options.ClaimAttemptKeyPrefix + id, c.AttemptKey(current), c.Options.AdmissionRecordsKey],
            [attempt.ToJsonString(), current.UserId, record.ToJsonString()]);
        return current with { AdmissionId = id };
    }
}

public sealed class Uuid7ClaimTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1108152157446L)]
    [InlineData(1792281600123L)]
    public async Task LuaExtractsNativeGuidTimestamp_WithoutMixedEndianConfusion(long timestamp)
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("user");
        var id = UuidRetentionTestSupport.At(timestamp);
        var keys = new RedisKeys(c.Options).ForClaim(id);
        var windowMs = (long)c.Options.ClaimIdAcceptanceWindowSeconds * 1000;
        var graceMs = (long)c.Options.ClaimAttemptGraceSeconds * 1000;
        var leaseMs = (long)c.Options.ProcessingLease.TotalMilliseconds;
        var source = "-- KEYS: extra documentation before helper functions.\n"
            + UuidRetentionTestSupport.ClaimScript;

        // Adjacent expired/accepted boundaries require Lua's decoded UUID time to equal timestamp exactly.
        foreach (var nowMs in new[] { timestamp + windowMs, timestamp + windowMs - 1 })
        {
            // Pin Redis TIME only in test-loaded source; production has no clock override.
            var script = source.Replace("local time = redis.call('TIME')",
                FormattableString.Invariant($"local time = {{{nowMs / 1000}, {(nowMs % 1000) * 1000}}}"),
                StringComparison.Ordinal);
            var parts = (RedisResult[])(await c.Db.ScriptEvaluateAsync(script, keys,
                [id.ToString(), c.Options.MaxActivePasses, leaseMs, windowMs, graceMs]))!;

            if (nowMs == timestamp + windowMs)
            {
                Assert.Single(parts);
                Assert.Equal("ID_EXPIRED", parts[0].ToString());
                Assert.False(await c.Db.KeyExistsAsync(keys[4]));
                Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
                continue;
            }

            Assert.Equal(6, parts.Length);
            Assert.Equal("CLAIMED", parts[0].ToString());
            Assert.Equal("user", parts[1].ToString());
            Assert.Equal(id.ToString(), parts[2].ToString());
            Assert.Equal(7, Guid.Parse(parts[2].ToString()).Version);
            Assert.Equal("1", parts[3].ToString());
            Assert.Equal(nowMs + leaseMs, long.Parse(parts[4].ToString(), CultureInfo.InvariantCulture));
            Assert.Equal(nowMs, long.Parse(parts[5].ToString(), CultureInfo.InvariantCulture));
            Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        }
    }

    [Theory]
    [InlineData(-300000, "ID_EXPIRED")]
    [InlineData(-299999, "CLAIMED")]
    [InlineData(0, "CLAIMED")]
    [InlineData(60000, "CLAIMED")]
    [InlineData(60001, "ID_TOO_FUTURE")]
    public async Task ExactBoundaries_UseRedisTimeAndNeverMutateOnRejection(long offset, string expected)
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("user");
        await c.Db.SortedSetAddAsync(c.Options.ActivePassesKey, "expired-active", 1);
        var now = await UuidRetentionTestSupport.NowAsync(c.Db);
        var id = UuidRetentionTestSupport.At(now + offset);
        // Pin a Redis TIME snapshot in TEST-LOADED source only. Production has no clock override.
        var source = UuidRetentionTestSupport.ClaimScript.Replace("local time = redis.call('TIME')",
            "local time = {math.floor(tonumber(ARGV[6])/1000), (tonumber(ARGV[6]) % 1000)*1000}");
        var attemptKey = c.Options.ClaimAttemptKeyPrefix + id;
        var result = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source,
            [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey, c.Options.ActivePassesKey, attemptKey],
            [id.ToString(), 10, 30000, 300000, 60000, now]))!;
        Assert.Equal(expected, result[0].ToString());
        if (expected != "CLAIMED")
        {
            Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
            Assert.False(await c.Db.KeyExistsAsync(attemptKey));
            Assert.Equal(1, (long)await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
            Assert.False(await c.Db.KeyExistsAsync(c.Options.AdmissionRecordsKey));
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("018f12345678-7000-8000-000000000000")]
    [InlineData("018f1234-5678-7000-0000-000000000000")]
    [InlineData("018F1234-5678-7000-8000-000000000000")]
    [InlineData("018f1234-5678-4000-8000-000000000000")]
    public async Task MissingInvalidOrUnsupportedIdentity_IsRejected(string id)
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("user");
        var result = (RedisResult[])(await c.Db.ScriptEvaluateAsync(UuidRetentionTestSupport.ClaimScript,
            [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey, c.Options.ActivePassesKey,
                c.Options.ClaimAttemptKeyPrefix + "invalid"], [id, 1, 30000, 300000, 60000]))!;
        Assert.StartsWith("ID_", result[0].ToString());
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task FreshNativeIdWorks_AndMissingV4CannotStartClaim()
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("user");
        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(
            new AdmissionId(Guid.NewGuid()), 10, c.Options.ProcessingLease));
        var id = UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db));
        Assert.Equal(id, (await c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease)).Claim!.AdmissionId);
    }

    [Fact]
    public async Task ConcurrentSameId_HasOneReservationAndOneUser()
    {
        var c = new Phase03Context(fixture);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => c.Queue.EnqueueAsync("user-" + i)));
        var id = UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db));
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease)));
        Assert.Single(results.Select(r => r.Claim).Distinct());
        Assert.Equal((9L, 1L, 0L), await c.CountsAsync());
    }
}

public sealed class BoundedAttemptLifecycleTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeExpiry_CannotTurnOldIdIntoNewClaim(bool full)
    {
        var c = new Phase03Context(fixture, capacity: 2, configure: UuidRetentionTestSupport.ShortPolicy);
        if (full) await c.ClaimAsync("held");
        var id = UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db));
        var first = await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease);
        Assert.Equal(full ? AdmissionClaimStatus.CapacityFull : AdmissionClaimStatus.QueueEmpty, first.Status);
        var key = c.Options.ClaimAttemptKeyPrefix + id;
        var created = Convert.ToInt64(id.ToString().Replace("-", "")[..12], 16);
        Assert.Equal(created + 2000, (long)await c.Db.ExecuteAsync("PEXPIRETIME", key));
        await c.Queue.EnqueueAsync("later");
        Assert.Equal(first, await c.Store.ClaimNextAsync(id, 2, c.Options.ProcessingLease));
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, key);
        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(id, 2, c.Options.ProcessingLease));
        Assert.False(await c.Db.KeyExistsAsync(key));
        var fresh = UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db));
        Assert.Equal("later", (await c.Store.ClaimNextAsync(fresh, 2, c.Options.ProcessingLease)).Claim!.UserId);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1500)]
    public async Task CommittedExpiry_ProtectsHistoryButDoesNotExtendPass(int passMilliseconds)
    {
        var c = new Phase03Context(fixture, configure: UuidRetentionTestSupport.ShortPolicy);
        var claim = await c.ClaimAsync();
        var now = await UuidRetentionTestSupport.NowAsync(c.Db);
        var expiry = now + passMilliseconds;
        await c.Store.CommitAsync(claim, "signed-token", DateTimeOffset.FromUnixTimeMilliseconds(expiry).UtcDateTime);
        var created = Convert.ToInt64(claim.AdmissionId.ToString().Replace("-", "")[..12], 16);
        var deadline = Math.Max(expiry, created + 1000) + 1000;
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.IdentityKey()));
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, "signed-token"));
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.TokenKey());
        Assert.True(await c.Db.KeyExistsAsync(c.AttemptKey(claim)));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, "signed-token"));
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.AttemptKey(claim));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, "signed-token"));
        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(claim.AdmissionId, 10, c.Options.ProcessingLease));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.Store.CommitAsync(claim, "replacement", DateTime.UtcNow.AddMinutes(5)));
        Assert.False(await c.Db.KeyExistsAsync(c.TokenKey()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldRecordedClaim_RemainsRecoverableAndTerminalReplayIsFenced(bool legacyV4)
    {
        var c = new Phase03Context(fixture, configure: UuidRetentionTestSupport.ShortPolicy);
        var id = legacyV4 ? new AdmissionId(Guid.NewGuid())
            : UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db) - 3600000);
        var claim = await UuidRetentionTestSupport.SeedRecordedAsync(c, id);
        Assert.Equal(claim, (await c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease)).Claim);
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PTTL", c.AttemptKey(claim)));
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PTTL", c.Options.AdmissionRecordsKey));
        claim = await c.ExpireLeaseAsync(claim);
        var before = await UuidRetentionTestSupport.NowAsync(c.Db);
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(claim));
        var after = await UuidRetentionTestSupport.NowAsync(c.Db);
        Assert.InRange((long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)), before + 1000, after + 1000);
        Assert.Equal((double)claim.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, claim.UserId));
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.AttemptKey(claim));
        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(claim));
        var freshId = UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db));
        var fresh = (await c.Store.ClaimNextAsync(freshId, 10, c.Options.ProcessingLease)).Claim!;
        Assert.Equal(claim.OriginalScore, fresh.OriginalScore);
        Assert.NotEqual(id, fresh.AdmissionId);
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, "old-token"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedOldId_CanCommitWithoutExtendingActive(bool legacyV4)
    {
        var c = new Phase03Context(fixture, configure: UuidRetentionTestSupport.ShortPolicy);
        var id = legacyV4 ? new AdmissionId(Guid.NewGuid())
            : UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db) - 3600000);
        var claim = await UuidRetentionTestSupport.SeedRecordedAsync(c, id);
        var expires = await c.CommitAsync(claim);
        var expiry = new DateTimeOffset(expires).ToUnixTimeMilliseconds();
        Assert.Equal(expiry + 1000, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, "test-token"));
    }

    [Theory]
    [InlineData("QUEUE_EMPTY")]
    [InlineData("CAPACITY_FULL")]
    [InlineData("RECOVERED")]
    public async Task RecordedLegacyTerminalOutcomes_RemainReadableWithoutImplicitMigration(string status)
    {
        var c = new Phase03Context(fixture);
        var id = new AdmissionId(Guid.NewGuid());
        var payload = System.Text.Json.JsonSerializer.Serialize(new { admissionId = id.ToString(), userId = "user", status });
        var key = c.Options.ClaimAttemptKeyPrefix + id;
        await c.Db.StringSetAsync(key, payload);
        if (status == "RECOVERED")
            await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease));
        else
            Assert.Equal(status == "QUEUE_EMPTY" ? AdmissionClaimStatus.QueueEmpty : AdmissionClaimStatus.CapacityFull,
                (await c.Store.ClaimNextAsync(id, 10, c.Options.ProcessingLease)).Status);
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PTTL", key));
        Assert.Equal(payload, (await c.Db.StringGetAsync(key)).ToString());
    }

    [Fact]
    public async Task MissingAttemptWhileProcessingOrActive_IsInconsistent()
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        await c.Db.KeyDeleteAsync(c.AttemptKey(claim));
        Assert.Equal(AdmissionCommitStatus.Inconsistent, await c.Store.GetCommitStatusAsync(claim, "token"));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(claim));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        var active = new Phase03Context(fixture);
        var committed = await active.ClaimAsync();
        await active.CommitAsync(committed);
        await active.Db.KeyDeleteAsync(active.AttemptKey(committed));
        Assert.Equal(AdmissionCommitStatus.Inconsistent, await active.Store.GetCommitStatusAsync(committed, "test-token"));
    }

    [Fact]
    public async Task MissingHistoryInsideWindow_RemainsUnknown()
    {
        var c = new Phase03Context(fixture);
        var claim = new AdmissionClaim(UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db)),
            "user", 1, DateTime.UtcNow.AddSeconds(30));
        Assert.Equal(AdmissionCommitStatus.Unknown, await c.Store.GetCommitStatusAsync(claim, "token"));
        Assert.Equal((0L, 0L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task ExistingV4ActiveJwt_RemainsValidAndColdLegacyAttemptIsNotMigrated()
    {
        var c = new Phase03Context(fixture);
        var claim = await UuidRetentionTestSupport.SeedRecordedAsync(c, new AdmissionId(Guid.NewGuid()));
        var issued = DateTimeOffset.FromUnixTimeSeconds(
            (await UuidRetentionTestSupport.NowAsync(c.Db)) / 1000).UtcDateTime;
        var data = new AdmissionTokenData(claim.AdmissionId, claim.UserId, issued, issued.AddMinutes(5));
        var token = c.Tokens.GenerateToken(data);
        await c.Store.CommitAsync(claim, token, data.ExpiresAtUtc);
        // Equivalent to an already committed pre-upgrade record, whose old writer assigned no TTL.
        await c.Db.KeyPersistAsync(c.AttemptKey(claim));
        var original = await c.Db.StringGetAsync(c.AttemptKey(claim));
        Assert.Equal(claim.AdmissionId, (await c.Tokens.ValidateTokenAsync(token))!.AdmissionId);
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, token));
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PTTL", c.AttemptKey(claim)));
        Assert.Equal(original, await c.Db.StringGetAsync(c.AttemptKey(claim)));
        var expiry = new DateTimeOffset(data.ExpiresAtUtc).ToUnixTimeMilliseconds();
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.IdentityKey()));
    }
}
