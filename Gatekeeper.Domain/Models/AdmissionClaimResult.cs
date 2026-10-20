using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Models;

public sealed record AdmissionClaimResult(
    AdmissionClaimStatus Status,
    AdmissionClaim? Claim)
{
    /// <summary>
    /// UTC time observed atomically by the claim authority. Redis supplies this on successful
    /// claims, including retries. Optional for existing custom stores; their clock remains host-owned.
    /// This observation does not change the claim identity or reservation deadline.
    /// </summary>
    public DateTime? ServerTimeUtc { get; init; }
}
