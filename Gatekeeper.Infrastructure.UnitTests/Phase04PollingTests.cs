using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase04PollingTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    internal static async Task<(AdmissionClaim Claim, string Token, DateTime Expiry)> ActivateAsync(
        Phase03Context c, string user = "user", int seconds = 300)
    {
        var claim = await c.ClaimAsync(user);
        var now = await UuidRetentionTestSupport.NowAsync(c.Db);
        var issued = DateTimeOffset.FromUnixTimeSeconds(now / 1000 - 1).UtcDateTime;
        var expiry = DateTimeOffset.FromUnixTimeSeconds(now / 1000 + seconds).UtcDateTime;
        var token = c.Tokens.GenerateToken(new(claim.AdmissionId, user, issued, expiry));
        await c.Store.CommitAsync(claim, token, expiry);
        return (claim, token, expiry);
    }

    [Fact]
    public async Task Lifecycle_IsDiscoverableWithoutNotification_AndReadOnly()
    {
        var c = new Phase03Context(fixture);
        Assert.Equal(AdmissionStatus.NotFound, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
        await c.Queue.EnqueueAsync("user");
        var score = await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user");
        Assert.Equal(AdmissionStatus.Waiting, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.Equal(score, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user"));
        var claim = (await c.Store.ClaimNextAsync(AdmissionId.New(), 10, c.Options.ProcessingLease)).Claim!;
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var token = c.Tokens.GenerateToken(new(claim.AdmissionId, "user", now, now.AddMinutes(5)));
        Assert.Equal(AdmissionStatus.Processing, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.False(await c.Db.KeyExistsAsync(c.TokenKey()));
        var lease = await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, "user");
        Assert.Equal(new DateTimeOffset(claim.LeaseExpiresAtUtc).ToUnixTimeMilliseconds(), lease);
        await c.Store.CommitAsync(claim, token, now.AddMinutes(5));
        var expiry = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey());
        var attempt = await c.Db.StringGetAsync(c.AttemptKey(claim));
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
            Assert.Equal(token, await c.Store.GetPassAsync("user"));
        }
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.IdentityKey()));
        Assert.Equal((double)expiry, await c.Db.SortedSetScoreAsync(c.Options.ActivePassesKey, "user"));
        Assert.Equal(attempt, await c.Db.StringGetAsync(c.AttemptKey(claim)));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.Store.CommitAsync(claim, "replacement", now.AddMinutes(10)));
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Theory]
    [InlineData("token-missing")]
    [InlineData("identity-missing")]
    [InlineData("active-missing")]
    [InlineData("waiting-conflict")]
    [InlineData("processing-conflict")]
    [InlineData("record-conflict")]
    [InlineData("token-persistent")]
    [InlineData("identity-expiry")]
    [InlineData("identity-malformed")]
    public async Task ContradictoryActiveState_DeniesDelivery(string fault)
    {
        var c = new Phase03Context(fixture);
        await ActivateAsync(c);
        switch (fault)
        {
            case "token-missing": await c.Db.KeyDeleteAsync(c.TokenKey()); break;
            case "identity-missing": await c.Db.KeyDeleteAsync(c.IdentityKey()); break;
            case "active-missing": await c.Db.SortedSetRemoveAsync(c.Options.ActivePassesKey, "user"); break;
            case "waiting-conflict": await c.Db.SortedSetAddAsync(c.Options.QueueKey, "user", 1); break;
            case "processing-conflict": await c.Db.SortedSetAddAsync(c.Options.ProcessingQueueKey, "user", 1); break;
            case "record-conflict": await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", "{}"); break;
            case "token-persistent": await c.Db.KeyPersistAsync(c.TokenKey()); break;
            case "identity-expiry": await c.Db.KeyExpireAsync(c.IdentityKey(), TimeSpan.FromMinutes(10)); break;
            case "identity-malformed": await SetKeepingExpiryAsync(c, c.IdentityKey(), "bad-id"); break;
        }
        Assert.Equal(AdmissionStatus.Inconsistent, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
    }

    [Theory]
    [InlineData("forged")]
    [InlineData("wrong-user")]
    [InlineData("wrong-id")]
    [InlineData("wrong-expiry")]
    [InlineData("expired-jwt")]
    public async Task StoredJwt_MustMatchCryptoAndCurrentRedisIdentity(string fault)
    {
        var c = new Phase03Context(fixture);
        var (claim, token, expiry) = await ActivateAsync(c);
        var issued = expiry.AddMinutes(-5);
        var data = new AdmissionTokenData(claim.AdmissionId, "user", issued, expiry);
        var bad = fault switch
        {
            "forged" => token[..^12] + "AAAAAAAAAAAA",
            "wrong-user" => c.Tokens.GenerateToken(data with { UserId = "other" }),
            "wrong-id" => c.Tokens.GenerateToken(data with { AdmissionId = AdmissionId.New() }),
            "wrong-expiry" => c.Tokens.GenerateToken(data with { ExpiresAtUtc = expiry.AddMinutes(1) }),
            _ => c.Tokens.GenerateToken(data with { IssuedAtUtc = issued.AddMinutes(-10), ExpiresAtUtc = issued.AddMinutes(-1) })
        };
        await SetKeepingExpiryAsync(c, c.TokenKey(), bad);
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("other"));
        // Active is a structural Redis observation; it never replaces GetPass validation.
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
    }

    [Fact]
    public async Task Expiration_LeavesHonestNotFound_AndDoesNotLeakCapacity()
    {
        var c = new Phase03Context(fixture, capacity: 1);
        var (_, token, _) = await ActivateAsync(c, seconds: 2);
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
        await UuidRetentionTestSupport.WaitUntilMissingAsync(c.Db, c.TokenKey());
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.Equal(AdmissionStatus.NotFound, await c.Store.GetStatusAsync("user"));
        Assert.NotNull(await c.Db.SortedSetScoreAsync(c.Options.ActivePassesKey, "user"));
        Assert.False(await c.Db.KeyExistsAsync(c.IdentityKey()));
        var next = await c.ClaimAsync("next");
        Assert.Equal("next", next.UserId);
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task RemovedActiveState_DeniesCryptographicallyValidPass()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await ActivateAsync(c);
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(token));
        await c.Db.ScriptEvaluateAsync("redis.call('ZREM',KEYS[1],ARGV[1]); redis.call('DEL',KEYS[2],KEYS[3]); return 1",
            [c.Options.ActivePassesKey, c.TokenKey(), c.IdentityKey()], ["user"]);
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(token));
        Assert.Equal(AdmissionStatus.NotFound, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeDuringCryptoValidation_IsFencedBeforeDelivery(bool replace)
    {
        var c = new Phase03Context(fixture);
        var (_, old, _) = await ActivateAsync(c);
        var validator = new InterceptTokens(c.Tokens, async () =>
        {
            await c.Db.SortedSetRemoveAsync(c.Options.ActivePassesKey, "user");
            await c.Db.KeyDeleteAsync([c.TokenKey(), c.IdentityKey()]);
            if (replace) await ActivateAsync(c);
        });
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, validator);
        Assert.Null(await store.GetPassAsync("user"));
        if (replace) Assert.NotEqual(old, await c.Store.GetPassAsync("user"));
    }

    [Fact]
    public async Task ExpiredProcessing_RemainsProcessingUntilRecovery_WithoutPollingMutation()
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ExpireLeaseAsync(await c.ClaimAsync());
        Assert.Equal(AdmissionStatus.Processing, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(claim));
        Assert.Equal(AdmissionStatus.Waiting, await c.Store.GetStatusAsync("user"));
        Assert.Equal((double)claim.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task WrongKeyTypes_FailClosedWithoutMutating(int key)
    {
        var c = new Phase03Context(fixture);
        string[] keys = [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey,
            c.Options.ActivePassesKey, c.TokenKey(), c.IdentityKey()];
        await c.Db.ListRightPushAsync(keys[key], "unexpected");
        Assert.Equal(AdmissionStatus.Inconsistent, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
        Assert.Equal("unexpected", (await c.Db.ListGetByIndexAsync(keys[key], 0)).ToString());
    }

    [Fact]
    public async Task StoppedRedisServer_ProducesFailureForBothPollingOperations()
    {
        await using var container = new RedisBuilder("redis:7.0").Build();
        await container.StartAsync();
        var config = ConfigurationOptions.Parse(container.GetConnectionString());
        config.AsyncTimeout = 500;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        var c = new Phase03Context(connection);
        await ActivateAsync(c);
        await container.StopAsync();
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Store.GetStatusAsync("user"));
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Store.GetPassAsync("user"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Polling_RejectsInvalidUser(string? user)
    {
        var c = new Phase03Context(fixture);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => c.Store.GetStatusAsync(user!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => c.Store.GetPassAsync(user!));
    }

    [Fact]
    public async Task Cancellation_IsHonoredBeforeReadsAndAfterValidation()
    {
        var c = new Phase03Context(fixture);
        await ActivateAsync(c);
        using var cancel = new CancellationTokenSource();
        var validator = new InterceptTokens(c.Tokens, () => { cancel.Cancel(); return Task.CompletedTask; });
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, validator);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPassAsync("user", cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetStatusAsync("user", cancel.Token));
        Assert.NotNull(await c.Store.GetPassAsync("user"));
    }

    [Fact]
    public async Task MultipleWorkersAndReaders_ObserveCommittedPassesWithinCapacity()
    {
        var c = new Phase03Context(fixture, capacity: 7, batchSize: 2);
        var users = Enumerable.Range(0, 24).Select(i => $"user-{i}").ToArray();
        foreach (var user in users) await c.Queue.EnqueueAsync(user);
        var workers = Enumerable.Range(0, 5).Select(_ => c.Worker(new CallbackNotifier(_ => Task.CompletedTask))).ToArray();
        var work = Task.WhenAll(workers.Select(w => w.ProcessBatchAsync()));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            do
            {
                foreach (var user in users)
                {
                    var status = await c.Store.GetStatusAsync(user);
                    Assert.NotEqual(AdmissionStatus.Inconsistent, status);
                    var token = await c.Store.GetPassAsync(user);
                    if (token is not null)
                    {
                        var data = await c.Tokens.ValidateTokenAsync(token);
                        Assert.NotNull(data);
                        Assert.Equal(user, data.UserId);
                    }
                }
                var counts = await c.CountsAsync();
                Assert.InRange(counts.Active + counts.Processing, 0, 7);
            } while (!work.IsCompleted);
        }));
        await work;
        Assert.Equal((17L, 0L, 7L), await c.CountsAsync());
        var active = new List<string>();
        foreach (var user in users)
            if (await c.Store.GetPassAsync(user) is { } token) active.Add(token);
        Assert.Equal(7, active.Distinct().Count());
    }

    internal static Task SetKeepingExpiryAsync(Phase03Context c, string key, string value)
        => c.Db.ScriptEvaluateAsync("return redis.call('SET',KEYS[1],ARGV[1],'KEEPTTL')", [key], [value]);

    [Fact]
    public async Task LegacyV4CommittedPass_IsRetrievableWithoutChangingAttemptRetention()
    {
        var c = new Phase03Context(fixture);
        var claim = await UuidRetentionTestSupport.SeedRecordedAsync(c, new AdmissionId(Guid.NewGuid()));
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var token = c.Tokens.GenerateToken(new(claim.AdmissionId, "user", issued, issued.AddMinutes(5)));
        await c.Store.CommitAsync(claim, token, issued.AddMinutes(5));
        await c.Db.KeyPersistAsync(c.AttemptKey(claim)); // Existing pre-upgrade persistent terminal record.
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
        Assert.Equal(-1, (long)await c.Db.ExecuteAsync("PTTL", c.AttemptKey(claim)));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Snapshot_UsesStrictRedisTimeBoundary(long offset, bool active)
    {
        var c = new Phase03Context(fixture);
        var (_, _, expiry) = await ActivateAsync(c);
        var instant = new DateTimeOffset(expiry).ToUnixTimeMilliseconds() + offset;
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "GetAdmissionSnapshot.lua"));
        // Deterministic boundary proof: only this test-loaded copy pins Redis TIME.
        source = source.Replace("redis.call('TIME')", "{ARGV[3],ARGV[4]}", StringComparison.Ordinal);
        var result = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source,
            [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey,
                c.Options.ActivePassesKey, c.TokenKey(), c.IdentityKey()],
            ["user", "PASS", instant / 1000, instant % 1000 * 1000]))!;
        Assert.Equal(active, result[0].ToString() == "ACTIVE");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"userId\":\"other\"}")]
    public async Task MalformedProcessingRecord_IsInconsistent(string record)
    {
        var c = new Phase03Context(fixture);
        await c.ClaimAsync();
        await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", record);
        Assert.Equal(AdmissionStatus.Inconsistent, await c.Store.GetStatusAsync("user"));
        Assert.Null(await c.Store.GetPassAsync("user"));
    }

    [Fact]
    public async Task ValidatorCannotSignDuringRepeatedRetrieval_AndMissingDependencyIsRejected()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await ActivateAsync(c);
        var validator = new InterceptTokens(c.Tokens, () => Task.CompletedTask);
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, validator);
        for (int i = 0; i < 5; i++) Assert.Equal(token, await store.GetPassAsync("user"));
        Assert.Equal(AdmissionStatus.Active, await store.GetStatusAsync("user"));
        Assert.Throws<ArgumentNullException>(() => new RedisAdmissionClaimStore(
            fixture.Connection, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, null!));
    }

    private sealed class InterceptTokens(IAdmissionTokenService inner, Func<Task> afterValidation) : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => throw new InvalidOperationException("Polling must never sign.");
        public async Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            var data = await inner.ValidateTokenAsync(token, cancellationToken);
            await afterValidation();
            return data;
        }
    }
}
