using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Services;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class FinalPassRedisKeyTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public void EveryScript_UsesExactOrderedKeys_AndOneClusterSlot()
    {
        var c = new Phase03Context(fixture);
        var o = c.Options;
        var id = AdmissionId.New();
        const string user = "user:{other-tag}:suffix";
        var keys = new RedisKeys(o);
        string pass = o.PassKeyPrefix + user, identity = o.ActiveAdmissionKeyPrefix + user, attempt = o.ClaimAttemptKeyPrefix + id;
        Assert.Equal(pass, keys.PassOf(user).ToString());
        Assert.Equal(identity, keys.ActiveIdentityOf(user).ToString());
        Assert.Equal(attempt, keys.AttemptOf(id).ToString());
        AssertKeys([o.QueueKey, o.ProcessingQueueKey, o.AdmissionRecordsKey, o.ActivePassesKey, pass, identity], keys.ForSnapshot(user));
        AssertKeys([o.QueueKey, o.ProcessingQueueKey, o.AdmissionRecordsKey, o.ActivePassesKey, attempt], keys.ForClaim(id));
        AssertKeys([o.ProcessingQueueKey, o.AdmissionRecordsKey, o.ActivePassesKey, pass, attempt, identity], keys.ForCommit(user, id));
        AssertKeys([attempt, o.ActivePassesKey, identity, pass, o.ProcessingQueueKey, o.AdmissionRecordsKey], keys.ForCommitStatus(user, id));
        AssertKeys([o.QueueKey, o.ProcessingQueueKey, o.AdmissionRecordsKey, o.ActivePassesKey, pass, identity, attempt], keys.ForRelease(user, id));
        AssertKeys([o.QueueKey, o.ProcessingQueueKey, o.AdmissionRecordsKey, o.ActivePassesKey, attempt, identity, pass], keys.ForRecover(user, id));
        AssertKeys([o.ProcessingQueueKey], keys.ForRecoveryScan());
        AssertKeys([o.QueueKey, o.QueueSequenceKey, o.ProcessingQueueKey, o.ActivePassesKey, o.AdmissionRecordsKey], keys.ForEnqueue());

        void AssertKeys(string[] expected, StackExchange.Redis.RedisKey[] actual)
        {
            Assert.Equal(expected, actual.Select(key => key.ToString()));
            Assert.Single(actual.Select(key => fixture.Connection.GetHashSlot(key)).Distinct());
        }
    }
}
