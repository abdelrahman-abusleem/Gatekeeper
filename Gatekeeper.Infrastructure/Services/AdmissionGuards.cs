using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using static Gatekeeper.Infrastructure.Services.RedisProtocolLimits;

namespace Gatekeeper.Infrastructure.Services;

internal static class AdmissionGuards
{
    internal static void EnsureAdmissionId(AdmissionId id, string parameterName)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("AdmissionId cannot be empty.", parameterName);
    }

    internal static void EnsureUtc(DateTime value, string parameterName, string message)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException(message, parameterName);
    }

    internal static long ValidateProcessingLease(TimeSpan processingLease)
    {
        double totalLeaseMilliseconds = processingLease.TotalMilliseconds;

        if (!double.IsFinite(totalLeaseMilliseconds) ||
            totalLeaseMilliseconds < 1 ||
            totalLeaseMilliseconds > MaxLeaseMilliseconds ||
            totalLeaseMilliseconds !=
                Math.Truncate(totalLeaseMilliseconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(processingLease),
                "Processing lease must be a whole number of milliseconds between 1 and 3600000.");
        }

        return (long)totalLeaseMilliseconds;
    }

    internal static void ValidateClaim(AdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim.UserId);

        EnsureAdmissionId(claim.AdmissionId, nameof(claim));

        if (claim.OriginalScore < 1 ||
            claim.OriginalScore > MaxExactScore ||
            claim.LeaseExpiresAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "Claim must have an exact positive sequence score and a UTC lease.",
                nameof(claim));
        }
    }
}
