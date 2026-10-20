using System.Globalization;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using static Gatekeeper.Infrastructure.Services.RedisProtocolLimits;

namespace Gatekeeper.Infrastructure.Services;

internal static class RedisAdmissionResponses
{
    internal enum SnapshotMode { Status, Pass }

    internal sealed record AdmissionSnapshot(
        AdmissionStatus Status,
        string? Identity = null,
        string? Token = null,
        long ExpiresAtMs = 0);

    internal static AdmissionSnapshot ParseAdmissionSnapshot(RedisResult result, SnapshotMode mode)
    {
        var parts = RedisResponse.CastArray(result, "Invalid admission snapshot response.");

        if (parts.Length == 0)
            throw new InvalidOperationException(
                "Invalid admission snapshot response.");

        if (mode == SnapshotMode.Pass &&
            parts.Length == 4 &&
            parts[0].ToString() == SnapshotStatuses.Active)
        {
            if (!Guid.TryParseExact(
                    parts[1].ToString(),
                    "D",
                    out var id) ||
                id == Guid.Empty ||
                parts[1].ToString() != id.ToString("D") ||
                !long.TryParse(
                    parts[3].ToString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expiry) ||
                expiry < 1 ||
                expiry > MaxUnixMilliseconds ||
                string.IsNullOrWhiteSpace(parts[2].ToString()))
            {
                return new(AdmissionStatus.Inconsistent);
            }

            return new(
                AdmissionStatus.Active,
                parts[1].ToString(),
                parts[2].ToString(),
                expiry);
        }

        if (parts.Length != 1)
            throw new InvalidOperationException(
                "Invalid admission snapshot response.");

        return new(parts[0].ToString() switch
        {
            SnapshotStatuses.Active when mode == SnapshotMode.Status => AdmissionStatus.Active,
            SnapshotStatuses.Waiting => AdmissionStatus.Waiting,
            SnapshotStatuses.Processing => AdmissionStatus.Processing,
            SnapshotStatuses.NotFound => AdmissionStatus.NotFound,
            SnapshotStatuses.Inconsistent => AdmissionStatus.Inconsistent,
            _ => throw new InvalidOperationException(
                "Unknown admission snapshot response.")
        });
    }

    internal static AdmissionClaimResult ParseClaimResult(RedisResult result, AdmissionId admissionId, ILogger<RedisAdmissionClaimStore> logger)
    {
        var parts = RedisResponse.ExpectNonEmptyArray(result,
            "Unexpected Redis claim response.", "Empty Redis claim response.");

        string status = parts[0].ToString();

        switch (status)
        {
            case ClaimStatuses.IdExpired or
                 ClaimStatuses.IdTooFuture or
                 ClaimStatuses.IdUnsupported or
                 ClaimStatuses.IdInvalid
                when parts.Length == 1:

                logger.LogDebug("Claim identity rejected ({Status}); AdmissionId {AdmissionId}.", status, admissionId);
                logger.LogWarning(
                    "Admission identity cannot initiate Claim ({Status}).",
                    status);

                throw new AdmissionClaimUnavailableException(
                    $"Admission identity rejected: {status}.");

            case ClaimStatuses.CapacityFull when parts.Length == 1:
                return new AdmissionClaimResult(
                    AdmissionClaimStatus.CapacityFull,
                    null);

            case ClaimStatuses.QueueEmpty when parts.Length == 1:
                return new AdmissionClaimResult(
                    AdmissionClaimStatus.QueueEmpty,
                    null);

            case ClaimStatuses.ClaimExpired when parts.Length == 1:
                logger.LogDebug("Claim lease expired; AdmissionId {AdmissionId}.", admissionId);
                logger.LogWarning(
                    "Admission claim has expired.");

                throw new AdmissionClaimUnavailableException(
                    "The admission claim lease has expired.");

            case ClaimStatuses.AttemptResolved when parts.Length == 1:
                logger.LogDebug("Claim already resolved; AdmissionId {AdmissionId}.", admissionId);
                logger.LogWarning(
                    "Admission claim is no longer in Processing.");

                throw new AdmissionClaimUnavailableException(
                    "The admission claim has already been resolved or replaced.");
        }

        if (status != ClaimStatuses.Claimed ||
            parts.Length != 6)
        {
            throw new InvalidOperationException(
                "Unknown Redis claim response.");
        }

        return ParseSuccessfulClaim(parts, admissionId, logger);
    }

    private static AdmissionClaimResult ParseSuccessfulClaim(RedisResult[] parts, AdmissionId admissionId, ILogger<RedisAdmissionClaimStore> logger)
    {
        string userId = parts[1].ToString();

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException(
                "Redis returned an invalid UserId.");
        }

        if (!Guid.TryParse(
                parts[2].ToString(),
                out var returnedId) ||
            returnedId != admissionId.Value)
        {
            throw new InvalidOperationException(
                "Redis returned an unexpected AdmissionId.");
        }

        if (!long.TryParse(
                parts[3].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var originalScore) ||
            originalScore < 1 ||
            originalScore > MaxExactScore)
        {
            throw new InvalidOperationException(
                "Redis returned an invalid original score.");
        }

        if (!long.TryParse(
                parts[4].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var leaseExpiresAtMs))
        {
            throw new InvalidOperationException(
                "Redis returned an invalid lease expiration.");
        }

        if (!long.TryParse(parts[5].ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var serverTimeMs)
            || serverTimeMs < 0 || serverTimeMs > MaxUnixMilliseconds
            || serverTimeMs >= leaseExpiresAtMs)
            throw new InvalidOperationException("Redis returned an invalid claim time.");

        var claim = new AdmissionClaim(
            admissionId,
            userId,
            originalScore,
            DateTimeOffset
                .FromUnixTimeMilliseconds(leaseExpiresAtMs)
                .UtcDateTime);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "Admission claim confirmed");
        }

        return new AdmissionClaimResult(
            AdmissionClaimStatus.Claimed,
            claim)
        {
            ServerTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(serverTimeMs).UtcDateTime
        };
    }

    internal static void EnsureCommitSucceeded(RedisResult result)
    {
        var status = RedisResponse.ExpectSingleStatus(result,
            "Unexpected Redis commit response.", "Unknown Redis commit response.");
        if (status == CommitStatuses.Committed) return;
        // Only documented script rejections are business outcomes. Unknown or malformed
        // replies must surface as protocol errors, with the pending attempt still retained.
        if (status is CommitStatuses.Inconsistent or CommitStatuses.NotProcessing or CommitStatuses.LeaseExpired or CommitStatuses.RecordMissing
            or CommitStatuses.AdmissionMismatch or CommitStatuses.AttemptMissing or CommitStatuses.AttemptMismatch or CommitStatuses.LeaseMismatch
            or CommitStatuses.OriginalScoreMismatch or CommitStatuses.InvalidPassExpiration or CommitStatuses.AlreadyActive
            or CommitStatuses.ActiveIdExists or CommitStatuses.TokenExists)
            throw new AdmissionCommitRejectedException($"Admission commit rejected: {status}");

        throw new InvalidOperationException("Unknown Redis commit response.");
    }

    internal static AdmissionCommitStatus ParseCommitStatus(RedisResult result)
    {
        return RedisResponse.ExpectSingleStatus(result,
            "Unexpected Redis commit status response.", "Invalid Redis commit status response.") switch
        {
            CommitOutcomeStatuses.Committed =>
                AdmissionCommitStatus.Committed,

            CommitOutcomeStatuses.NotCommitted =>
                AdmissionCommitStatus.NotCommitted,

            CommitOutcomeStatuses.Unknown =>
                AdmissionCommitStatus.Unknown,

            CommitOutcomeStatuses.Mismatch =>
                AdmissionCommitStatus.Mismatch,

            CommitOutcomeStatuses.Inconsistent =>
                AdmissionCommitStatus.Inconsistent,

            CommitOutcomeStatuses.Expired =>
                AdmissionCommitStatus.Expired,

            _ => throw new InvalidOperationException(
                "Unknown Redis commit status.")
        };
    }

    internal static AdmissionValidationStatus ParseValidation(RedisResult response)
    {
        var status = RedisResponse.ExpectSingleStatus(response, "Invalid admission validation response.");

        return status switch
        {
            SnapshotStatuses.Valid => AdmissionValidationStatus.Valid,
            SnapshotStatuses.Inconsistent => AdmissionValidationStatus.Inconsistent,
            SnapshotStatuses.Inactive or SnapshotStatuses.Waiting or SnapshotStatuses.Processing or SnapshotStatuses.NotFound
                => AdmissionValidationStatus.Inactive,
            _ => throw new InvalidOperationException(
                "Unknown admission validation response.")
        };
    }

    internal static AdmissionReleaseStatus ParseRelease(RedisResult response)
    {
        var status = RedisResponse.ExpectSingleStatus(response, "Invalid admission release response.");

        return status switch
        {
            ReleaseStatuses.Released => AdmissionReleaseStatus.Released,
            ReleaseStatuses.NotCurrent => AdmissionReleaseStatus.NotCurrent,
            ReleaseStatuses.Inconsistent => AdmissionReleaseStatus.Inconsistent,
            _ => throw new InvalidOperationException(
                "Unknown admission release response.")
        };
    }

    internal static AdmissionRecoveryStatus ParseRecovery(RedisResult result)
    {
        var status = RedisResponse.ExpectSingleStatus(result, "Unexpected Redis recovery response.");

        return status switch
        {
            RecoveryStatuses.Recovered => AdmissionRecoveryStatus.Recovered,
            RecoveryStatuses.NotExpired => AdmissionRecoveryStatus.NotExpired,
            RecoveryStatuses.AlreadyResolved => AdmissionRecoveryStatus.AlreadyResolved,
            RecoveryStatuses.Inconsistent => AdmissionRecoveryStatus.Inconsistent,
            _ => throw new InvalidOperationException(
                "Unknown Redis recovery response.")
        };
    }
}
