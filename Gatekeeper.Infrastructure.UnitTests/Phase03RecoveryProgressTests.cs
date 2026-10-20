using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03RecoveryProgressTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(5, 3, 2)]
    [InlineData(4, 4, 2)]
    public async Task CorruptPrefix_DoesNotStarveValidExpiredReservations(int corruptCount, int validCount, int batchSize)
    {
        var (c, corrupt, valid) = await SeedAsync(corruptCount, validCount);
        var recovered = new List<string>();
        for (int scan = 0; scan < (corruptCount + validCount) * 4; scan++)
        {
            var results = await c.Store.RecoverExpiredAsync(batchSize);
            Assert.InRange(results.Count, 0, batchSize);
            recovered.AddRange(results.Where(x => x.Status == AdmissionRecoveryStatus.Recovered).Select(x => x.UserId));
            var (waiting, processing, active) = await c.CountsAsync();
            Assert.Equal(corruptCount + validCount, waiting + processing + active);
            Assert.InRange(processing + active, 0, c.Options.MaxActivePasses);
            if (recovered.Count == validCount) break;
        }
        Assert.Equal(validCount, recovered.Count);
        Assert.Equal(validCount, recovered.Distinct().Count());
        Assert.Equal((validCount, (long)corruptCount, 0L), await c.CountsAsync());
        await AssertCorruptUnchangedAsync(c, corrupt);
        var waitingEntries = await c.Db.SortedSetRangeByRankWithScoresAsync(c.Options.QueueKey);
        Assert.Equal(valid.Select(x => x.UserId), waitingEntries.Select(x => x.Element.ToString()));
        Assert.Equal(valid.Select(x => (double)x.OriginalScore), waitingEntries.Select(x => x.Score));
        // Only released capacity is available; corrupt reservations still count.
        foreach (var old in valid)
        {
            var fresh = (await c.Store.ClaimNextAsync(AdmissionId.New(), c.Options.MaxActivePasses, c.Options.ProcessingLease)).Claim!;
            Assert.Equal(old.UserId, fresh.UserId);
            Assert.Equal(old.OriginalScore, fresh.OriginalScore);
            Assert.NotEqual(old.AdmissionId, fresh.AdmissionId);
        }
        await c.Queue.EnqueueAsync("extra");
        Assert.Equal(AdmissionClaimStatus.CapacityFull,
            (await c.Store.ClaimNextAsync(AdmissionId.New(), c.Options.MaxActivePasses, c.Options.ProcessingLease)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentScanners_ProgressBeyondCorruption_WithoutDuplicateRecovery(bool independentStores)
    {
        var (c, corrupt, valid) = await SeedAsync(5, 5);
        var stores = Enumerable.Range(0, 4).Select(_ => independentStores
            ? new Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore(fixture.Connection,
                Microsoft.Extensions.Options.Options.Create(c.Options),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore>.Instance, c.Tokens)
            : c.Store).ToArray();
        var recovered = new List<string>();
        for (int round = 0; round < 20 && recovered.Count < valid.Length; round++)
        {
            var results = await Task.WhenAll(stores.Select(store => store.RecoverExpiredAsync(2)));
            Assert.All(results, page => Assert.InRange(page.Count, 0, 2));
            recovered.AddRange(results.SelectMany(x => x).Where(x => x.Status == AdmissionRecoveryStatus.Recovered).Select(x => x.UserId));
        }
        Assert.Equal(valid.Length, recovered.Count);
        Assert.Equal(valid.Length, recovered.Distinct().Count());
        Assert.Equal((5L, 5L, 0L), await c.CountsAsync());
        await AssertCorruptUnchangedAsync(c, corrupt);
    }

    private async Task<(Phase03Context Context, AdmissionClaim[] Corrupt, AdmissionClaim[] Valid)> SeedAsync(int corruptCount, int validCount)
    {
        var c = new Phase03Context(fixture, capacity: corruptCount + validCount);
        var all = new List<AdmissionClaim>();
        for (int index = 0; index < corruptCount + validCount; index++)
            all.Add(await c.ExpireLeaseAsync(await c.ClaimAsync($"user-{index:D3}")));
        // All leases share the same score, so pagination must handle ZSET member tie ordering.
        var deadline = all.Min(x => x.LeaseExpiresAtUtc);
        var deadlineMs = new DateTimeOffset(deadline).ToUnixTimeMilliseconds();
        for (int index = 0; index < all.Count; index++)
        {
            var claim = all[index] with { LeaseExpiresAtUtc = deadline };
            var attempt = System.Text.Json.Nodes.JsonNode.Parse((await c.Db.StringGetAsync(c.AttemptKey(claim))).ToString())!;
            attempt["leaseExpiresAt"] = deadlineMs;
            await c.Db.SortedSetAddAsync(c.Options.ProcessingQueueKey, claim.UserId, deadlineMs);
            await c.Db.StringSetAsync(c.AttemptKey(claim), attempt.ToJsonString());
            all[index] = claim;
            if (index < corruptCount)
            {
                switch (index % 3)
                {
                    case 0: await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, claim.UserId, "{corrupt"); break;
                    case 1: await c.Db.HashDeleteAsync(c.Options.AdmissionRecordsKey, claim.UserId); break;
                    case 2: await c.Db.KeyDeleteAsync(c.AttemptKey(claim)); break;
                }
            }
        }
        return (c, all.Take(corruptCount).ToArray(), all.Skip(corruptCount).ToArray());
    }

    private static async Task AssertCorruptUnchangedAsync(Phase03Context c, AdmissionClaim[] corrupt)
    {
        for (int index = 0; index < corrupt.Length; index++)
        {
            var claim = corrupt[index];
            Assert.Equal((double)new DateTimeOffset(claim.LeaseExpiresAtUtc).ToUnixTimeMilliseconds(),
                await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, claim.UserId));
            var record = await c.Db.HashGetAsync(c.Options.AdmissionRecordsKey, claim.UserId);
            if (index % 3 == 0) Assert.Equal("{corrupt", record.ToString());
            else if (index % 3 == 1) Assert.True(record.IsNull);
            else Assert.Contains(claim.AdmissionId.ToString(), record.ToString());
            Assert.Equal(index % 3 != 2, await c.Db.KeyExistsAsync(c.AttemptKey(claim)));
            Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, claim.UserId));
        }
    }
}
