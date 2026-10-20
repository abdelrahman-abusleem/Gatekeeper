using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.Models;

public sealed record AdmissionClaim(
    AdmissionId AdmissionId,
    string UserId,
    long OriginalScore,
    DateTime LeaseExpiresAtUtc);