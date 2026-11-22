using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Models;

/// <summary>A definitive claim result with a reservation only when Claimed.</summary>
/// <param name="Status">Whether a reservation was claimed, capacity was full, or Waiting was empty.</param>
/// <param name="Claim">The confirmed reservation for Claimed; otherwise null.</param>
public sealed record AdmissionClaimResult(
    AdmissionClaimStatus Status,
    AdmissionClaim? Claim)
{
    /// <summary>UTC observation captured atomically by the authority; optional for custom stores, whose host clock is then used. It never changes the identity or reservation deadline.</summary>
    public DateTime? ServerTimeUtc { get; init; }
}
