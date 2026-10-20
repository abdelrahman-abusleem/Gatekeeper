using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Infrastructure.Workers;

internal sealed class AdmissionLane
{
    internal AdmissionId? AttemptId { get; private set; }
    internal AdmissionClaim? Claim { get; private set; }
    internal string? Token { get; private set; }
    private DateTime _issuedAtUtc;
    internal DateTime ExpiresAtUtc { get; private set; }
    internal bool CommitUncertain { get; private set; }
    private int _failures;
    private long _failedAt;
    private TimeSpan _retryDelay;

    internal AdmissionId EnsureAttemptId()
    {
        AttemptId ??= AdmissionId.New();
        return AttemptId.Value;
    }
    internal void MarkCommitUncertain() => CommitUncertain = true;
    internal bool IsBackingOff(TimeProvider clock) => _retryDelay > TimeSpan.Zero
        && clock.GetElapsedTime(_failedAt) < _retryDelay;
    internal void RegisterFailure(AdmissionRetryPolicy retry, TimeProvider clock)
    {
        _failures = Math.Min(AdmissionRetryPolicy.MaxFailures, _failures + 1);
        _retryDelay = retry.Delay(_failures);
        _failedAt = clock.GetTimestamp();
    }

    internal void PrepareToken(AdmissionClaimResult result, IAdmissionTokenService tokens,
        TimeProvider clock, TimeSpan passDuration)
    {
        Claim = result.Claim ?? throw new InvalidOperationException("Confirmed claim is missing.");
        if (Claim.AdmissionId != AttemptId!.Value)
            throw new InvalidOperationException("Confirmed claim identity differs from the pending attempt.");
        if (ExpiresAtUtc == default)
        {
            // Redis owns deadlines. Round its Claim observation down to JWT seconds and
            // retain these exact dates and token across signing/Commit retries. Older
            // custom stores without ServerTimeUtc retain their host-owned clock behavior.
            var authorityTime = result.ServerTimeUtc ?? clock.GetUtcNow().UtcDateTime;
            if (authorityTime.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException("Claim authority time must be UTC.");
            _issuedAtUtc = DateTimeOffset.FromUnixTimeSeconds(
                new DateTimeOffset(authorityTime).ToUnixTimeSeconds()).UtcDateTime;
            ExpiresAtUtc = _issuedAtUtc.Add(passDuration);
        }
        Token ??= tokens.GenerateToken(new AdmissionTokenData(
            Claim.AdmissionId, Claim.UserId, _issuedAtUtc, ExpiresAtUtc));
    }

    internal void Clear()
    {
        AttemptId = null; Claim = null; Token = null; CommitUncertain = false; ExpiresAtUtc = default;
        _failures = 0; _retryDelay = TimeSpan.Zero;
    }
}
