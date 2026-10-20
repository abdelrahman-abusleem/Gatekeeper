using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Services;

internal sealed class RedisKeys(QueueOptions options)
{
    internal RedisKey Waiting => options.QueueKey;
    internal RedisKey Processing => options.ProcessingQueueKey;
    internal RedisKey Records => options.AdmissionRecordsKey;
    internal RedisKey Active => options.ActivePassesKey;
    internal RedisKey Sequence => options.QueueSequenceKey;
    internal RedisKey PassOf(string userId) => options.PassKeyPrefix + userId;
    internal RedisKey ActiveIdentityOf(string userId) => options.ActiveAdmissionKeyPrefix + userId;
    internal RedisKey AttemptOf(AdmissionId id) => options.ClaimAttemptKeyPrefix + id;

    // GetAdmissionSnapshot.lua KEYS[1..6]: Waiting, Processing, Records, Active, token, identity.
    internal RedisKey[] ForSnapshot(string userId) =>
        [Waiting, Processing, Records, Active, PassOf(userId), ActiveIdentityOf(userId)];

    // ClaimNextUser.lua KEYS[1..5]: Waiting, Processing, Records, Active, attempt.
    internal RedisKey[] ForClaim(AdmissionId id) => [Waiting, Processing, Records, Active, AttemptOf(id)];

    // CommitAdmission.lua KEYS[1..6]: Processing, Records, Active, token, attempt, identity.
    internal RedisKey[] ForCommit(string userId, AdmissionId id) =>
        [Processing, Records, Active, PassOf(userId), AttemptOf(id), ActiveIdentityOf(userId)];

    // GetCommitStatus.lua KEYS[1..6]: attempt, Active, identity, token, Processing, Records.
    internal RedisKey[] ForCommitStatus(string userId, AdmissionId id) =>
        [AttemptOf(id), Active, ActiveIdentityOf(userId), PassOf(userId), Processing, Records];

    // ReleaseAdmission.lua KEYS[1..7]: Waiting, Processing, Records, Active, token, identity, expected attempt.
    internal RedisKey[] ForRelease(string userId, AdmissionId id) =>
        [Waiting, Processing, Records, Active, PassOf(userId), ActiveIdentityOf(userId), AttemptOf(id)];

    // RecoverAdmission.lua KEYS[1..7]: Waiting, Processing, Records, Active, attempt, identity, token.
    internal RedisKey[] ForRecover(string userId, AdmissionId id) =>
        [Waiting, Processing, Records, Active, AttemptOf(id), ActiveIdentityOf(userId), PassOf(userId)];

    // GetExpiredProcessing.lua KEYS[1]: Processing. Rank cursor and count are ARGV, never keys.
    internal RedisKey[] ForRecoveryScan() => [Processing];

    // EnqueueUser.lua KEYS[1..5]: Waiting, sequence, Processing, Active, Records.
    internal RedisKey[] ForEnqueue() => [Waiting, Sequence, Processing, Active, Records];
}
