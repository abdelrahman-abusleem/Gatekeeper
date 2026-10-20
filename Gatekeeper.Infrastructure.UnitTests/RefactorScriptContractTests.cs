using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Services;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorScriptContractTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task RawClaimContract_BindsIdentityScoreLeaseAndAuthorityTimeInOrder()
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("contract-user");
        var id = AdmissionId.New();
        var before = await UuidRetentionTestSupport.NowAsync(c.Db);
        var reply = (RedisResult[])(await c.Db.ScriptEvaluateAsync(RedisScriptSource.Load("ClaimNextUser.lua").Value,
            [c.Options.QueueKey, c.Options.ProcessingQueueKey, c.Options.AdmissionRecordsKey,
             c.Options.ActivePassesKey, c.Options.ClaimAttemptKeyPrefix + id],
            [id.ToString(), 1, 30000, 300000, 60000]))!;
        Assert.Equal(6, reply.Length);
        Assert.Equal("CLAIMED", reply[0].ToString());
        Assert.Equal("contract-user", reply[1].ToString());
        Assert.Equal(id.ToString(), reply[2].ToString());
        Assert.Equal("1", reply[3].ToString());
        Assert.Equal((long)reply[5] + 30000, (long)reply[4]);
        Assert.InRange((long)reply[5], before, await UuidRetentionTestSupport.NowAsync(c.Db));
        Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "contract-user"));
        Assert.Equal((double)(long)reply[4], await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, "contract-user"));
    }

    [Theory]
    [InlineData(5, "1000")]
    [InlineData(6, "not-ms")]
    [InlineData(6, "253402300800000")]
    [InlineData(6, "2000")]
    public void MalformedAuthorityResponse_IsNeverAccepted(int fields, string observed)
    {
        var c = new Phase03Context(fixture);
        var id = AdmissionId.New();
        RedisValue[] values = ["CLAIMED", "user", id.ToString(), "1", "2000", observed];
        var response = RedisResult.Create(values.Take(fields).ToArray());
        Assert.Throws<InvalidOperationException>(() => RedisAdmissionResponses.ParseClaimResult(response, id,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisAdmissionClaimStore>.Instance));
    }
}
