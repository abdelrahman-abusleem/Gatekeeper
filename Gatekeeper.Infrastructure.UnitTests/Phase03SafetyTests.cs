using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03SafetyTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData("waiting")]
    [InlineData("processing")]
    [InlineData("records")]
    [InlineData("active")]
    [InlineData("attempt")]
    [InlineData("identity")]
    [InlineData("token")]
    public async Task RecoveryWrongType_IsDetectedBeforeAnyTransition(string part)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ExpireLeaseAsync(await c.ClaimAsync());
        var key = part switch
        {
            "waiting" => c.Options.QueueKey,
            "processing" => c.Options.ProcessingQueueKey,
            "records" => c.Options.AdmissionRecordsKey,
            "active" => c.Options.ActivePassesKey,
            "attempt" => c.AttemptKey(claim),
            "identity" => c.IdentityKey(),
            _ => c.TokenKey()
        };
        await c.Db.KeyDeleteAsync(key);
        await c.Db.ListRightPushAsync(key, "wrong-type");
        Assert.Equal(AdmissionRecoveryStatus.Inconsistent, await c.Store.RecoverAsync(claim));
        Assert.Equal(RedisType.List, await c.Db.KeyTypeAsync(key));
        if (part != "processing") Assert.NotNull(await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, "user"));
        if (part != "waiting") Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "user"));
        if (part != "records") Assert.False((await c.Db.HashGetAsync(c.Options.AdmissionRecordsKey, "user")).IsNull);
    }

    [Theory]
    [InlineData("record", "{broken")]
    [InlineData("record", "true")]
    [InlineData("record", "{}")]
    [InlineData("attempt", "{broken")]
    [InlineData("attempt", "42")]
    [InlineData("attempt", "{}")]
    public async Task CommitMalformedMetadata_CannotPartiallyPromote(string part, string content)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        if (part == "record") await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", content);
        else await c.Db.StringSetAsync(c.AttemptKey(claim), content);
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(claim));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        Assert.False(await c.Db.KeyExistsAsync(c.TokenKey()));
        Assert.False(await c.Db.KeyExistsAsync(c.IdentityKey()));
    }

    [Theory]
    [InlineData("score")]
    [InlineData("lease")]
    public async Task CommitRequiresExactClaimSnapshot(string field)
    {
        var c = new Phase03Context(fixture);
        var claim = await c.ClaimAsync();
        var wrong = field == "score" ? claim with { OriginalScore = claim.OriginalScore + 1 }
            : claim with { LeaseExpiresAtUtc = claim.LeaseExpiresAtUtc.AddMilliseconds(1) };
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(wrong));
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task ExternalBoundaries_RejectDefaultAdmissionIdentityAndInvalidLease()
    {
        var c = new Phase03Context(fixture);
        var invalid = new AdmissionClaim(default, "user", 1, DateTime.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => c.Store.ClaimNextAsync(default, 1, TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => c.Store.CommitAsync(invalid, "token", DateTime.UtcNow.AddMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => c.Store.GetCommitStatusAsync(invalid, "token"));
        await Assert.ThrowsAsync<ArgumentException>(() => c.Store.RecoverAsync(invalid));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.Store.ClaimNextAsync(AdmissionId.New(), 1, TimeSpan.FromHours(2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.Store.ClaimNextAsync(AdmissionId.New(), 1, TimeSpan.FromTicks(1)));
        Assert.Equal((0L, 0L, 0L), await c.CountsAsync());
    }
}
