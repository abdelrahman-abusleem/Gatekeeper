using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.Models;

/// <summary>An immutable Processing reservation returned by the claim authority.</summary>
/// <param name="AdmissionId">The unique attempt identity.</param>
/// <param name="UserId">The host-authorized user identity.</param>
/// <param name="OriginalScore">The exact positive FIFO sequence restored by Recovery.</param>
/// <param name="LeaseExpiresAtUtc">The UTC Processing lease deadline owned by the authority.</param>
public sealed record AdmissionClaim(
    AdmissionId AdmissionId,
    string UserId,
    long OriginalScore,
    DateTime LeaseExpiresAtUtc);