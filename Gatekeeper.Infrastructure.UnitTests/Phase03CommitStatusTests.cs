using Gatekeeper.Domain.Exceptions;
using System.Text.Json.Nodes;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03CommitStatusTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task LostCommitResponse_CanBeConfirmedWithoutWritingAgain()
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        await c.CommitAsync(claim);
        var original = await c.Db.StringGetAsync(c.TokenKey());
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, "test-token"));
        Assert.Equal(original, await c.Db.StringGetAsync(c.TokenKey()));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Theory]
    [InlineData("token")]
    [InlineData("identity")]
    [InlineData("expiry")]
    [InlineData("user")]
    [InlineData("admission")]
    public async Task MismatchedCurrentState_IsNeverConfirmed(string part)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        await c.CommitAsync(claim);
        switch (part)
        {
            case "token": await c.Db.StringSetAsync(c.TokenKey(), "different-token"); break;
            case "identity": await c.Db.StringSetAsync(c.IdentityKey(), AdmissionId.New().ToString()); break;
            case "expiry": await c.Db.SortedSetAddAsync(c.Options.ActivePassesKey, "user", 123); break;
            case "user": claim = claim with { UserId = "someone-else" }; break;
            case "admission": claim = claim with { AdmissionId = AdmissionId.New() }; break;
        }
        Assert.NotEqual(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, "test-token"));
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("token")]
    [InlineData("identity")]
    [InlineData("active")]
    [InlineData("malformed")]
    [InlineData("json-scalar")]
    [InlineData("wrong-type")]
    [InlineData("ttl")]
    [InlineData("processing")]
    public async Task MissingOrCorruptState_FailsClosed(string part)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        await c.CommitAsync(claim);
        switch (part)
        {
            case "attempt": await c.Db.KeyDeleteAsync(c.AttemptKey(claim)); break;
            case "token": await c.Db.KeyDeleteAsync(c.TokenKey()); break;
            case "identity": await c.Db.KeyDeleteAsync(c.IdentityKey()); break;
            case "active": await c.Db.SortedSetRemoveAsync(c.Options.ActivePassesKey, "user"); break;
            case "malformed": await c.Db.StringSetAsync(c.AttemptKey(claim), "{bad-json"); break;
            case "json-scalar": await c.Db.StringSetAsync(c.AttemptKey(claim), "true"); break;
            case "wrong-type":
                await c.Db.KeyDeleteAsync(c.TokenKey());
                await c.Db.ListRightPushAsync(c.TokenKey(), "value"); break;
            case "ttl": await c.Db.KeyPersistAsync(c.TokenKey()); break;
            case "processing": await c.Db.SortedSetAddAsync(c.Options.ProcessingQueueKey, "user", 123); break;
        }
        Assert.NotEqual(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, "test-token"));
    }

    [Fact]
    public async Task HistoricalCommit_AfterExpiryOrNewAdmission_IsNotCurrentAuthorization()
    {
        var c = new Phase03Context(fixture);
        var old = await c.ClaimAsync();
        await c.CommitAsync(old);
        var attempt = JsonNode.Parse((await c.Db.StringGetAsync(c.AttemptKey(old))).ToString())!;
        attempt["passExpiresAt"] = 1;
        await c.Db.StringSetAsync(c.AttemptKey(old), attempt.ToJsonString());
        await c.Db.KeyDeleteAsync([c.TokenKey(), c.IdentityKey()]);
        await c.Db.SortedSetAddAsync(c.Options.ActivePassesKey, "user", 1);
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(old, "test-token"));
        var current = await c.ClaimAsync();
        Assert.NotEqual(old.AdmissionId, current.AdmissionId);
        await c.CommitAsync(current, "new-token");
        Assert.Equal(AdmissionCommitStatus.Mismatch, await c.Store.GetCommitStatusAsync(old, "test-token"));
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(current, "new-token"));
    }

    [Fact]
    public async Task ProcessingIsNotCommitted_AndExpiredLeaseIsTerminal()
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        Assert.Equal(AdmissionCommitStatus.NotCommitted, await c.Store.GetCommitStatusAsync(claim, "test-token"));
        claim = await c.ExpireLeaseAsync(claim);
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, "test-token"));
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("records")]
    [InlineData("active")]
    [InlineData("token")]
    [InlineData("attempt")]
    [InlineData("identity")]
    public async Task CommitWithWrongTypeKey_DoesNotPartiallyWrite(string part)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        var key = part switch
        {
            "waiting" => c.Options.ProcessingQueueKey,
            "records" => c.Options.AdmissionRecordsKey,
            "active" => c.Options.ActivePassesKey,
            "token" => c.TokenKey(),
            "attempt" => c.AttemptKey(claim),
            _ => c.IdentityKey()
        };
        await c.Db.KeyDeleteAsync(key);
        await c.Db.ListRightPushAsync(key, "wrong-type");
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(claim));
        Assert.Equal(RedisType.List, await c.Db.KeyTypeAsync(key));
        if (part != "active") Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.ActivePassesKey, "user"));
        if (part != "token") Assert.False(await c.Db.KeyExistsAsync(c.TokenKey()));
        if (part != "identity") Assert.False(await c.Db.KeyExistsAsync(c.IdentityKey()));
        if (part != "waiting") Assert.NotNull(await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, "user"));
    }
}
