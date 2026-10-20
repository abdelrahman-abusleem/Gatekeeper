using static Gatekeeper.Infrastructure.Services.RedisAdmissionResponses;
using static Gatekeeper.Infrastructure.Services.AdmissionGuards;

using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Services;

public sealed class RedisAdmissionClaimStore : IAdmissionClaimStore
{
    private static readonly Lazy<string> RecoveryScript = RedisScriptSource.Load("RecoverAdmission.lua");
    private static readonly Lazy<string> AdmissionSnapshotScript = RedisScriptSource.Load("GetAdmissionSnapshot.lua");
    private static readonly Lazy<string> ReleaseScript = RedisScriptSource.Load("ReleaseAdmission.lua");
    private static readonly Lazy<string> ClaimScript = RedisScriptSource.Load("ClaimNextUser.lua");
    private static readonly Lazy<string> CommitScript = RedisScriptSource.Load("CommitAdmission.lua");
    private static readonly Lazy<string> GetCommitStatusScript = RedisScriptSource.Load("GetCommitStatus.lua");
    private static readonly Lazy<string> ExpiredProcessingScript = RedisScriptSource.Load("GetExpiredProcessing.lua");

    private readonly IDatabase _database;
    private readonly RedisKeys _keys;
    private readonly QueueOptions _options;
    private readonly ILogger<RedisAdmissionClaimStore> _logger;
    private readonly SemaphoreSlim _recoveryScanLock = new(1, 1);
    private long _recoveryScanOffset;
    private readonly IAdmissionTokenService _tokens;

    private long AcceptanceWindowMilliseconds => (long)_options.ClaimIdAcceptanceWindowSeconds * 1000;
    private long AttemptGraceMilliseconds => (long)_options.ClaimAttemptGraceSeconds * 1000;

    public RedisAdmissionClaimStore(
        IConnectionMultiplexer redis,
        IOptions<QueueOptions> options,
        ILogger<RedisAdmissionClaimStore> logger,
        IAdmissionTokenService tokens)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(tokens);

        _database = redis.GetDatabase();
        _options = options.Value;
        _keys = new RedisKeys(_options);
        _options.ValidateRedisKeys();

        if (!_options.HasValidRetentionPolicy())
            throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName,
                typeof(QueueOptions),
                ["Invalid attempt retention policy."]);

        _logger = logger;
        _tokens = tokens;
    }

    public async Task<AdmissionStatus> GetStatusAsync(
        string userId,
        CancellationToken cancellationToken = default)
        => (await ReadSnapshotAsync(userId, SnapshotMode.Status, cancellationToken)).Status;

    public async Task<AdmissionValidationStatus> ValidateAdmissionAsync(
        string signedToken,
        string expectedUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUserId);
        cancellationToken.ThrowIfCancellationRequested();

        var data = await _tokens.ValidateTokenAsync(signedToken, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (data is null || data.IssuedAtUtc.Kind != DateTimeKind.Utc
            || data.ExpiresAtUtc.Kind != DateTimeKind.Utc)
            return AdmissionValidationStatus.InvalidToken;

        if (!string.Equals(data.UserId, expectedUserId, StringComparison.Ordinal))
            return AdmissionValidationStatus.SubjectMismatch;

        var response = await AdmissionMetrics.RedisAsync(
            AdmissionMetricOperations.Validate,
            () => _database.ScriptEvaluateAsync(
                AdmissionSnapshotScript.Value,
                keys: _keys.ForSnapshot(expectedUserId),
                // ARGV[1..5]: user, VALIDATE, ID, exact JWT, UTC expiry ms.
                // Reply: one VALID/INACTIVE/WAITING/PROCESSING/NOT_FOUND/INCONSISTENT status.
                values:
                [
                    expectedUserId,
                    SnapshotModes.Validate,
                    data.AdmissionId.ToString(),
                    signedToken,
                    new DateTimeOffset(data.ExpiresAtUtc).ToUnixTimeMilliseconds()
                ]));

        cancellationToken.ThrowIfCancellationRequested();

        return ParseValidation(response);
    }

    public async Task<AdmissionReleaseStatus> ReleaseAsync(
        string userId,
        AdmissionId expectedAdmissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        EnsureAdmissionId(expectedAdmissionId, nameof(expectedAdmissionId));

        cancellationToken.ThrowIfCancellationRequested();

        var response = await AdmissionMetrics.RedisAsync(
            AdmissionMetricOperations.Release,
            () => _database.ScriptEvaluateAsync(
                ReleaseScript.Value,
                // KEYS[1..7]: Waiting, Processing, Records, Active, token, identity, expected attempt.
                // ARGV[1..2]: user, expected ID. Reply: RELEASED/NOT_CURRENT/INCONSISTENT.
                keys: _keys.ForRelease(userId, expectedAdmissionId),
                values:
                [
                    userId,
                    expectedAdmissionId.ToString()
                ]));

        cancellationToken.ThrowIfCancellationRequested();

        return ParseRelease(response);
    }

    public async Task<string?> GetPassAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await ReadSnapshotAsync(
            userId,
            SnapshotMode.Pass,
            cancellationToken);

        if (snapshot.Status != AdmissionStatus.Active)
            return null;

        var data = await _tokens.ValidateTokenAsync(snapshot.Token!, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!MatchesPass(snapshot, data, userId))
            return null;

        // Token validation can await external work. Re-read authority to fence a Release
        // or replacement during that await; the first snapshot alone is insufficient.
        return await ReadSnapshotAsync(userId, SnapshotMode.Pass, cancellationToken)
            == snapshot
            ? snapshot.Token
            : null;
    }

    private static bool MatchesPass(AdmissionSnapshot snapshot, AdmissionTokenData? data, string userId)
        => data is not null
            && data.IssuedAtUtc.Kind == DateTimeKind.Utc
            && data.ExpiresAtUtc.Kind == DateTimeKind.Utc
            && data.UserId == userId
            && data.AdmissionId.ToString() == snapshot.Identity
            && new DateTimeOffset(data.ExpiresAtUtc).ToUnixTimeMilliseconds() == snapshot.ExpiresAtMs;

    private async Task<AdmissionSnapshot> ReadSnapshotAsync(
        string userId,
        SnapshotMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await AdmissionMetrics.RedisAsync(
            mode == SnapshotMode.Pass ? AdmissionMetricOperations.PassSnapshot : AdmissionMetricOperations.StatusSnapshot,
            () => _database.ScriptEvaluateAsync(
                AdmissionSnapshotScript.Value,
                keys: _keys.ForSnapshot(userId),
                // ARGV[1..2]: user, STATUS/PASS. STATUS replies with one structural status;
                // PASS replies ACTIVE, canonical ID, exact JWT, expiry ms; otherwise one status.
                values:
                [
                    userId,
                    mode == SnapshotMode.Pass ? SnapshotModes.Pass : SnapshotModes.Status
                ]));

        cancellationToken.ThrowIfCancellationRequested();

        return ParseAdmissionSnapshot(result, mode);
    }

    public async Task<AdmissionClaimResult> ClaimNextAsync(
        AdmissionId admissionId,
        int maxActivePasses,
        TimeSpan processingLease,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmissionId(admissionId, nameof(admissionId));

        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxActivePasses, 1);

        long leaseMilliseconds = ValidateProcessingLease(processingLease);

        cancellationToken.ThrowIfCancellationRequested();

        var result = await AdmissionMetrics.RedisAsync(
            AdmissionMetricOperations.Claim,
            () => _database.ScriptEvaluateAsync(
                ClaimScript.Value,
                // KEYS[1..5]: Waiting, Processing, Records, Active, this attempt.
                // ARGV[1..5]: ID, capacity, lease ms, acceptance ms, grace ms.
                // CLAIMED reply: status,user,ID,OriginalScore,lease expiry ms,Redis observation ms.
                // Other replies are one capacity/empty/terminal-ID status; malformed state is an error.
                keys: _keys.ForClaim(admissionId),
                values:
                [
                    admissionId.ToString(),
                    maxActivePasses,
                    leaseMilliseconds,
                    AcceptanceWindowMilliseconds,
                    AttemptGraceMilliseconds
                ]));

        return ParseClaimResult(result, admissionId, _logger);
    }

    public async Task CommitAsync(
        AdmissionClaim claim,
        string signedToken,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedToken);

        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc), "Expiration must be UTC.");

        cancellationToken.ThrowIfCancellationRequested();

        long expiresAtMs = new DateTimeOffset(
            expiresAtUtc).ToUnixTimeMilliseconds();

        var result = await AdmissionMetrics.RedisAsync(
            AdmissionMetricOperations.Commit,
            () => _database.ScriptEvaluateAsync(
                CommitScript.Value,
                // KEYS[1..6]: Processing, Records, Active, token, attempt, identity.
                // ARGV[1..8]: user, ID, exact JWT, expiry ms, OriginalScore, lease expiry ms,
                // acceptance ms, grace ms. Reply: COMMITTED or one definitive rejection status.
                keys: _keys.ForCommit(claim.UserId, claim.AdmissionId),
                values:
                [
                    claim.UserId,
                    claim.AdmissionId.ToString(),
                    signedToken,
                    expiresAtMs,
                    claim.OriginalScore,
                    new DateTimeOffset(claim.LeaseExpiresAtUtc)
                        .ToUnixTimeMilliseconds(),
                    AcceptanceWindowMilliseconds,
                    AttemptGraceMilliseconds
                ]));

        EnsureCommitSucceeded(result);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Admission committed");
    }

    public async Task<AdmissionCommitStatus> GetCommitStatusAsync(
        AdmissionClaim claim,
        string signedToken,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(signedToken);

        cancellationToken.ThrowIfCancellationRequested();

        var result = await AdmissionMetrics.RedisAsync(
            AdmissionMetricOperations.CommitStatus,
            () => _database.ScriptEvaluateAsync(
                GetCommitStatusScript.Value,
                // KEYS[1..6]: attempt, Active, identity, token, Processing, Records.
                // ARGV[1..5]: user, ID, exact JWT, acceptance ms, grace ms. One status:
                // COMMITTED/NOT_COMMITTED/UNKNOWN/MISMATCH/INCONSISTENT/EXPIRED.
                keys: _keys.ForCommitStatus(claim.UserId, claim.AdmissionId),
                values:
                [
                    claim.UserId,
                    claim.AdmissionId.ToString(),
                    signedToken,
                    AcceptanceWindowMilliseconds,
                    AttemptGraceMilliseconds
                ]));

        return ParseCommitStatus(result);
    }

    public async Task<AdmissionRecoveryStatus> RecoverAsync(
        AdmissionClaim claim,
        CancellationToken cancellationToken = default)
    {
        ValidateClaim(claim);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await AdmissionMetrics.RedisAsync(
            AdmissionMetricOperations.Recover,
            () => _database.ScriptEvaluateAsync(
                RecoveryScript.Value,
                // KEYS[1..7]: Waiting, Processing, Records, Active, attempt, identity, token.
                // ARGV[1..6]: user, ID, OriginalScore, observed lease expiry ms, acceptance ms, grace ms.
                // Reply: RECOVERED/NOT_EXPIRED/ALREADY_RESOLVED/INCONSISTENT; no speculative repair.
                keys: _keys.ForRecover(claim.UserId, claim.AdmissionId),
                values:
                [
                    claim.UserId,
                    claim.AdmissionId.ToString(),
                    claim.OriginalScore,
                    new DateTimeOffset(claim.LeaseExpiresAtUtc)
                        .ToUnixTimeMilliseconds(),
                    AcceptanceWindowMilliseconds,
                    AttemptGraceMilliseconds
                ]));

        return ParseRecovery(result);
    }

    public async Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var users = await SelectExpiredUsersAsync(
            batchSize,
            cancellationToken);

        var results = new List<AdmissionRecoveryResult>(users.Length);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            results.Add(await RecoverUserAsync(user, cancellationToken));
        }

        return results;
    }

    private async Task<AdmissionRecoveryResult> RecoverUserAsync(RedisResult user, CancellationToken cancellationToken)
    {
        var userId = user.ToString();

        var recordRead = _database.HashGetAsync(
            _keys.Records,
            userId);

        var leaseRead = _database.SortedSetScoreAsync(
            _keys.Processing,
            userId);

        await Task.WhenAll(recordRead, leaseRead);

        var record = await recordRead;
        var lease = await leaseRead;

        AdmissionRecoveryStatus status;

        if (lease is null && record.IsNull)
        {
            status = AdmissionRecoveryStatus.AlreadyResolved;
        }
        else if (!AdmissionRecordReader.TryReadClaim(
                     userId,
                     record,
                     lease,
                     out var claim))
        {
            status = AdmissionRecoveryStatus.Inconsistent;
        }
        else
        {
            status = await RecoverAsync(
                claim!,
                cancellationToken);
        }

        if (status == AdmissionRecoveryStatus.Inconsistent)
        {
            _logger.LogWarning(
                "Processing metadata requires intervention.");
        }

        return new AdmissionRecoveryResult(userId, status);
    }

    private async Task<RedisResult[]> SelectExpiredUsersAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        await _recoveryScanLock.WaitAsync(cancellationToken);

        try
        {
            var response = await AdmissionMetrics.RedisAsync(
                AdmissionMetricOperations.RecoveryScan,
                () => _database.ScriptEvaluateAsync(
                    ExpiredProcessingScript.Value,
                    // KEYS[1]: Processing. ARGV[1..2]: page size, rotating rank offset.
                    // Reply: {next offset, {expired user IDs}}; selection is not Recovery itself.
                    keys: _keys.ForRecoveryScan(),
                    values:
                    [
                        batchSize,
                        _recoveryScanOffset
                    ]));

            var parts = RedisResponse.CastArray(response, "Unexpected Redis recovery scan response.");

            if (parts is not { Length: 2 } ||
                parts[1].Resp2Type != ResultType.Array)
            {
                throw new InvalidOperationException(
                    "Unexpected Redis recovery scan response.");
            }

            _recoveryScanOffset = (long)parts[0];

            return (RedisResult[])parts[1]!;
        }
        finally
        {
            _recoveryScanLock.Release();
        }
    }

}