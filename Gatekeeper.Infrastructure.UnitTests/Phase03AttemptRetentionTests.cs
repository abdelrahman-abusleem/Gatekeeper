using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Xunit.Abstractions;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03AttemptRetentionTests(RedisClaimFixture fixture, ITestOutputHelper output) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData("CLAIMED")]
    [InlineData("QUEUE_EMPTY")]
    [InlineData("CAPACITY_FULL")]
    [InlineData("COMMITTED")]
    [InlineData("RECOVERED")]
    public async Task EveryAttemptOutcome_RetainsReplayFenceForApprovedLifetime(string status)
    {
        var c = new Phase03Context(fixture, capacity: 1);
        AdmissionClaim? claim = null;
        AdmissionId id;
        if (status == "QUEUE_EMPTY")
        {
            id = AdmissionId.New();
            Assert.Equal(AdmissionClaimStatus.QueueEmpty,
                (await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease)).Status);
        }
        else if (status == "CAPACITY_FULL")
        {
            await c.ClaimAsync();
            id = AdmissionId.New();
            Assert.Equal(AdmissionClaimStatus.CapacityFull,
                (await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease)).Status);
        }
        else
        {
            claim = await c.ClaimAsync();
            id = claim.AdmissionId;
            if (status == "COMMITTED") await c.CommitAsync(claim);
            if (status == "RECOVERED")
            {
                claim = await c.ExpireLeaseAsync(claim);
                Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(claim));
            }
        }
        var key = c.Options.ClaimAttemptKeyPrefix + id;
        var before = await c.Db.StringGetAsync(key);
        Assert.False(before.IsNull);
        var expiry = (long)await c.Db.ExecuteAsync("PEXPIRETIME", key);
        if (status == "CLAIMED") Assert.Equal(-1, expiry);
        else
        {
            var created = Convert.ToInt64(id.ToString().Replace("-", "")[..12], 16);
            long expected = created + 360000;
            if (status == "COMMITTED")
            {
                using var payload = System.Text.Json.JsonDocument.Parse(before.ToString());
                expected = Math.Max(expected, payload.RootElement.GetProperty("passExpiresAt").GetInt64() + 60000);
            }
            Assert.Equal(expected, expiry);
        }
        var memory = (long)await c.Db.ExecuteAsync("MEMORY", "USAGE", key);
        Assert.True(memory > 0);
        output.WriteLine("Status={0}; MEMORY USAGE={1} bytes; attempt key UTF8 bytes={2}; PTTL={3}",
            status, memory, System.Text.Encoding.UTF8.GetByteCount(key), (long)await c.Db.ExecuteAsync("PTTL", key));
        await c.Queue.EnqueueAsync("later");
        // Raising the test capacity makes new work possible without erasing the old identity.
        if (status is "COMMITTED" or "RECOVERED")
            await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => c.Store.ClaimNextAsync(id, 2, c.Options.ProcessingLease));
        else
        {
            var retry = await c.Store.ClaimNextAsync(id, 2, c.Options.ProcessingLease);
            if (status == "CLAIMED") Assert.Equal(claim, retry.Claim);
            else Assert.Equal(status == "QUEUE_EMPTY" ? AdmissionClaimStatus.QueueEmpty : AdmissionClaimStatus.CapacityFull, retry.Status);
        }
        Assert.Equal(before, await c.Db.StringGetAsync(key));
        Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", key));
        Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, "later"));
        Assert.NotNull(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "later"));
    }
}
