using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

internal sealed class Phase03Context
{
    public QueueOptions Options { get; }
    public IDatabase Db { get; }
    public RedisAdmissionClaimStore Store { get; }
    public RedisWaitingQueue Queue { get; }
    public JwtAdmissionTokenService Tokens { get; }

    public Phase03Context(RedisClaimFixture fixture, int capacity = 10, int batchSize = 4,
        Action<QueueOptions>? configure = null)
        : this(fixture.Connection, capacity, batchSize, configure) { }

    public Phase03Context(IConnectionMultiplexer connection, int capacity = 10, int batchSize = 4,
        Action<QueueOptions>? configure = null)
    {
        var prefix = $"phase03:{{{Guid.NewGuid():N}}}";
        Options = new QueueOptions
        {
            QueueKey = prefix + ":waiting",
            ProcessingQueueKey = prefix + ":processing",
            AdmissionRecordsKey = prefix + ":records",
            ActivePassesKey = prefix + ":active",
            QueueSequenceKey = prefix + ":sequence",
            ClaimAttemptKeyPrefix = prefix + ":attempt:",
            PassKeyPrefix = prefix + ":pass:",
            ActiveAdmissionKeyPrefix = prefix + ":identity:",
            NotifyChannelPrefix = prefix + ":notify:",
            MaxActivePasses = capacity,
            BatchSize = batchSize,
            ReleaseIntervalSeconds = 1
        };
        configure?.Invoke(Options);
        Db = connection.GetDatabase();
        Queue = new RedisWaitingQueue(connection,
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<RedisWaitingQueue>.Instance);
        Tokens = new JwtAdmissionTokenService(Microsoft.Extensions.Options.Options.Create(new JwtOptions
        {
            SecretKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()),
            Issuer = "phase03",
            Audience = "phase03-clients"
        }), NullLogger<JwtAdmissionTokenService>.Instance);
        Store = new RedisAdmissionClaimStore(connection,
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<RedisAdmissionClaimStore>.Instance, Tokens);
    }

    public QueueProcessorWorker Worker(IAdmissionNotifier notifier, IAdmissionClaimStore? store = null,
        IAdmissionTokenService? tokens = null) => new(store ?? Store, tokens ?? Tokens, notifier,
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<QueueProcessorWorker>.Instance);

    public async Task<AdmissionClaim> ClaimAsync(string userId = "user")
    {
        await Queue.EnqueueAsync(userId);
        return (await Store.ClaimNextAsync(AdmissionId.New(), Options.MaxActivePasses,
            Options.ProcessingLease)).Claim!;
    }

    public async Task<DateTime> CommitAsync(AdmissionClaim claim, string token = "test-token")
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300).UtcDateTime;
        await Store.CommitAsync(claim, token, expires);
        return expires;
    }

    public string AttemptKey(AdmissionClaim claim) => Options.ClaimAttemptKeyPrefix + claim.AdmissionId;
    public string TokenKey(string user = "user") => Options.PassKeyPrefix + user;
    public string IdentityKey(string user = "user") => Options.ActiveAdmissionKeyPrefix + user;

    // Controlled Redis-state fixture, not a simulated wall-clock/network partition.
    public async Task<AdmissionClaim> ExpireLeaseAsync(AdmissionClaim claim)
    {
        var time = (RedisResult[])(await Db.ExecuteAsync("TIME"))!;
        var expired = long.Parse(time[0].ToString(), CultureInfo.InvariantCulture) * 1000 - 1;
        var attempt = JsonNode.Parse((await Db.StringGetAsync(AttemptKey(claim))).ToString())!;
        attempt["leaseExpiresAt"] = expired;
        await Db.ScriptEvaluateAsync(
            "redis.call('ZADD', KEYS[1], ARGV[1], ARGV[2]); redis.call('SET', KEYS[2], ARGV[3]); return 1",
            [Options.ProcessingQueueKey, AttemptKey(claim)], [expired, claim.UserId, attempt.ToJsonString()]);
        return claim with { LeaseExpiresAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(expired).UtcDateTime };
    }

    public async Task<(long Waiting, long Processing, long Active)> CountsAsync()
    {
        var result = (RedisResult[])(await Db.ScriptEvaluateAsync(
            "return {redis.call('ZCARD',KEYS[1]),redis.call('ZCARD',KEYS[2]),redis.call('ZCARD',KEYS[3])}",
            [Options.QueueKey, Options.ProcessingQueueKey, Options.ActivePassesKey]))!;
        return ((long)result[0], (long)result[1], (long)result[2]);
    }
}

internal sealed class CallbackNotifier(Func<string, Task> callback) : IAdmissionNotifier
{
    public Task NotifyReadyAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return callback(userId);
    }
}

internal sealed class FaultingClaimStore(IAdmissionClaimStore inner) : IAdmissionClaimStore
{
    public Task<AdmissionValidationStatus> ValidateAdmissionAsync(string token, string user, CancellationToken ct = default)
        => inner.ValidateAdmissionAsync(token, user, ct);
    public Task<AdmissionReleaseStatus> ReleaseAsync(string user, AdmissionId id, CancellationToken ct = default)
        => inner.ReleaseAsync(user, id, ct);
    public Task<AdmissionStatus> GetStatusAsync(string user, CancellationToken ct = default) => inner.GetStatusAsync(user, ct);
    public Task<string?> GetPassAsync(string user, CancellationToken ct = default) => inner.GetPassAsync(user, ct);
    public int LostClaimResponses { get; set; }
    public int LostCommitResponses { get; set; }
    public int UnavailableClaimCalls { get; set; }
    public int UnavailableStatusCalls { get; set; }
    public int UnavailableCommitCalls { get; set; }
    public AdmissionCommitStatus? ForcedStatus { get; set; }
    public Action? AfterClaim { get; set; }
    public Action? AfterCommit { get; set; }
    public Func<AdmissionClaim, Task>? BeforeCommit { get; set; }
    public List<AdmissionId> ClaimIds { get; } = [];
    public List<string> CommitTokens { get; } = [];

    public async Task<AdmissionClaimResult> ClaimNextAsync(AdmissionId id, int max, TimeSpan lease, CancellationToken ct = default)
    {
        ClaimIds.Add(id);
        if (UnavailableClaimCalls-- > 0) throw new TimeoutException("Simulated unavailable Redis before Claim.");
        var result = await inner.ClaimNextAsync(id, max, lease, ct);
        AfterClaim?.Invoke();
        if (LostClaimResponses-- > 0) throw new TimeoutException("Simulated lost Claim response after execution.");
        return result;
    }

    public async Task CommitAsync(AdmissionClaim claim, string token, DateTime expiry, CancellationToken ct = default)
    {
        CommitTokens.Add(token);
        if (UnavailableCommitCalls-- > 0) throw new TimeoutException("Simulated unavailable Redis before Commit.");
        if (BeforeCommit is not null) await BeforeCommit(claim);
        await inner.CommitAsync(claim, token, expiry, ct);
        AfterCommit?.Invoke();
        ct.ThrowIfCancellationRequested();
        if (LostCommitResponses-- > 0) throw new TimeoutException("Simulated lost Commit response after execution.");
    }

    public Task<AdmissionCommitStatus> GetCommitStatusAsync(AdmissionClaim claim, string token, CancellationToken ct = default)
    {
        if (UnavailableStatusCalls-- > 0) throw new TimeoutException("Simulated unavailable reconciliation.");
        return ForcedStatus.HasValue ? Task.FromResult(ForcedStatus.Value) : inner.GetCommitStatusAsync(claim, token, ct);
    }

    public Task<AdmissionRecoveryStatus> RecoverAsync(AdmissionClaim claim, CancellationToken ct = default) => inner.RecoverAsync(claim, ct);
    public Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(int count, CancellationToken ct = default)
        => inner.RecoverExpiredAsync(count, ct);
}
