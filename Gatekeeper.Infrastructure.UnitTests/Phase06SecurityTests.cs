using System.Text.Json.Nodes;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase06SecurityTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task ExactCommittedPass_IsRequired_AndValidationNeverRenewsExpiry()
    {
        var c = new Phase03Context(fixture);
        var (claim, token, expiry) = await Phase04PollingTests.ActivateAsync(c);
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey());
        for (int i = 0; i < 5; i++)
            Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        var alternative = MakeToken(claim.AdmissionId, expiry, "extra-claim");
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(alternative));
        Assert.NotEqual(token, alternative);
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(alternative, "user"));
        Assert.Equal(AdmissionValidationStatus.SubjectMismatch, await c.Store.ValidateAdmissionAsync(token, "other"));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.IdentityKey()));
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("type")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("malformed-id")]
    [InlineData("missing-id")]
    [InlineData("unsigned")]
    [InlineData("algorithm")]
    [InlineData("malformed")]
    public async Task CryptoFailures_NeverBecomeRedisAuthorization(string fault)
    {
        var c = new Phase03Context(fixture);
        var (claim, _, expiry) = await Phase04PollingTests.ActivateAsync(c);
        Assert.Equal(AdmissionValidationStatus.InvalidToken,
            await c.Store.ValidateAdmissionAsync(MakeToken(claim.AdmissionId, expiry, fault), "user"));
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task MissingToken_IsDenied(string? token)
    {
        var c = new Phase03Context(fixture);
        Assert.Equal(AdmissionValidationStatus.InvalidToken, await c.Store.ValidateAdmissionAsync(token!, "user"));
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("processing")]
    [InlineData("missing")]
    public async Task ValidCryptoWithoutActiveState_DoesNotAuthorize(string state)
    {
        var c = new Phase03Context(fixture);
        var id = AdmissionId.New();
        if (state == "waiting") await c.Queue.EnqueueAsync("user");
        if (state == "processing") id = (await c.ClaimAsync()).AdmissionId;
        var expiry = DateTime.UtcNow.AddMinutes(5);
        var token = MakeToken(id, expiry, "normal");
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(token));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("token")]
    [InlineData("active")]
    [InlineData("expiry")]
    [InlineData("processing")]
    public async Task ContradictoryRedisState_IsNotOrdinaryInactiveOrValid(string fault)
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        if (fault == "identity") await c.Db.KeyDeleteAsync(c.IdentityKey());
        if (fault == "token") await c.Db.KeyDeleteAsync(c.TokenKey());
        if (fault == "active") await c.Db.SortedSetRemoveAsync(c.Options.ActivePassesKey, "user");
        if (fault == "expiry") await c.Db.KeyPersistAsync(c.TokenKey());
        if (fault == "processing") await c.Db.SortedSetAddAsync(c.Options.ProcessingQueueKey, "user", 1);
        Assert.Equal(AdmissionValidationStatus.Inconsistent, await c.Store.ValidateAdmissionAsync(token, "user"));
    }

    [Fact]
    public async Task ExpiredRedisState_DeniesStillCryptographicallyValidJwt()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        await c.Db.ScriptEvaluateAsync("redis.call('DEL',KEYS[1],KEYS[2]); redis.call('ZADD',KEYS[3],1,ARGV[1]); return 1",
            [c.TokenKey(), c.IdentityKey(), c.Options.ActivePassesKey], ["user"]);
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(token));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
    }

    [Fact]
    public async Task ValidationDuringCommit_ObservesOnlyInactiveThenValid()
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var token = c.Tokens.GenerateToken(new(claim.AdmissionId, "user", issued, issued.AddMinutes(5)));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
        var commit = c.Store.CommitAsync(claim, token, issued.AddMinutes(5));
        var reads = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => c.Store.ValidateAdmissionAsync(token, "user")));
        await commit;
        Assert.All(reads, value => Assert.Contains(value, new[] { AdmissionValidationStatus.Inactive, AdmissionValidationStatus.Valid }));
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
    }

    [Fact]
    public async Task ValidateMode_ReturnsNoJwt_AndDoesNotWrite()
    {
        var c = new Phase03Context(fixture);
        var (claim, token, expiry) = await Phase04PollingTests.ActivateAsync(c);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "GetAdmissionSnapshot.lua"));
        var response = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source,
            [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey, c.Options.ActivePassesKey,
                c.TokenKey(), c.IdentityKey()], ["user", "VALIDATE", claim.AdmissionId.ToString(), token,
                new DateTimeOffset(expiry).ToUnixTimeMilliseconds()]))!;
        Assert.Equal("VALID", Assert.Single(response).ToString());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task ServerOutage_ThrowsDuringValidationAndRelease()
    {
        await using var container = new RedisBuilder("redis:7.0").Build();
        await container.StartAsync();
        var config = ConfigurationOptions.Parse(container.GetConnectionString());
        config.AsyncTimeout = 500; config.BacklogPolicy = BacklogPolicy.FailFast;
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        var c = new Phase03Context(connection);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        await container.StopAsync();
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Store.ValidateAdmissionAsync(token, "user"));
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Store.ReleaseAsync("user", claim.AdmissionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisCommandTimeout_NeverReturnsSuccess_EvenIfReleaseEventuallyExecutes(bool release)
    {
        await using var container = new RedisBuilder("redis:7.0").Build();
        await container.StartAsync();
        using var admin = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString() + ",allowAdmin=true");
        var config = ConfigurationOptions.Parse(container.GetConnectionString());
        config.AsyncTimeout = 200;
        config.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        var c = new Phase03Context(connection);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim));
        // Real server-side command pause in this test's own disposable container, not a network partition.
        await admin.GetDatabase().ExecuteAsync("CLIENT", "PAUSE", "2000", "ALL");
        try
        {
            var error = release
                ? await Assert.ThrowsAsync<RedisTimeoutException>(() => c.Store.ReleaseAsync("user", claim.AdmissionId))
                : await Assert.ThrowsAsync<RedisTimeoutException>(() => c.Store.ValidateAdmissionAsync(token, "user"));
            Assert.DoesNotContain(token, error.ToString());
        }
        finally { await admin.GetDatabase().ExecuteAsync("CLIENT", "UNPAUSE"); }
        // Only an authoritative conditional evaluation may settle an uncertain Release.
        if (release)
        {
            var outcome = await c.Store.ReleaseAsync("user", claim.AdmissionId);
            Assert.Contains(outcome, new[] { AdmissionReleaseStatus.Released, AdmissionReleaseStatus.NotCurrent });
            Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, "user"));
        }
        else Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
    }

    [Fact]
    public async Task ReleaseDuringAsyncCryptoValidation_DeniesAtFinalRedisDecision()
    {
        var c = new Phase03Context(fixture);
        var (claim, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var validator = new CallbackTokens(c.Tokens, async () => { await c.Store.ReleaseAsync("user", claim.AdmissionId); });
        var store = new Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore(fixture.Connection,
            Microsoft.Extensions.Options.Options.Create(c.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore>.Instance, validator);
        Assert.Equal(AdmissionValidationStatus.Inactive, await store.ValidateAdmissionAsync(token, "user"));
    }

    [Fact]
    public async Task CancellationAfterCrypto_RemainsCancellationWithoutRedisMutation()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        using var canceled = new CancellationTokenSource();
        var validator = new CallbackTokens(c.Tokens, () => { canceled.Cancel(); return Task.CompletedTask; });
        var store = new Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore(fixture.Connection,
            Microsoft.Extensions.Options.Options.Create(c.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore>.Instance, validator);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ValidateAdmissionAsync(token, "user", canceled.Token));
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
    }

    private sealed class CallbackTokens(Gatekeeper.Domain.Abstractions.IAdmissionTokenService inner, Func<Task> callback)
        : Gatekeeper.Domain.Abstractions.IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => throw new InvalidOperationException("Validation must not sign.");
        public async Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            var data = await inner.ValidateTokenAsync(token, cancellationToken);
            await callback();
            return data;
        }
    }

    internal static string MakeToken(AdmissionId id, DateTime expiry, string fault)
    {
        if (fault == "malformed") return "invalid.jwt";
        var claims = new Dictionary<string, object> { ["sub"] = "user", ["jti"] = id.ToString(), ["type"] = "admission_pass" };
        if (fault == "missing-id") claims.Remove("jti");
        if (fault == "malformed-id") claims["jti"] = "not-an-id";
        if (fault == "type") claims["type"] = "login";
        if (fault == "extra-claim") claims["extra"] = "different committed bytes";
        var issued = DateTime.UtcNow.AddMinutes(-1);
        if (fault == "expired") { issued = DateTime.UtcNow.AddMinutes(-3); expiry = DateTime.UtcNow.AddMinutes(-2); }
        if (fault == "future") { issued = DateTime.UtcNow.AddMinutes(1); expiry = DateTime.UtcNow.AddMinutes(2); }
        var key = Enumerable.Range(1, fault == "algorithm" ? 64 : 32).Select(i => (byte)i).ToArray();
        if (fault == "signature") key[0]++;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = expiry,
            Issuer = fault == "issuer" ? "other" : "phase03",
            Audience = fault == "audience" ? "other" : "phase03-clients",
            SigningCredentials = fault == "unsigned" ? null : new SigningCredentials(new SymmetricSecurityKey(key),
                fault == "algorithm" ? SecurityAlgorithms.HmacSha512 : SecurityAlgorithms.HmacSha256)
        });
    }
}
