using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.Models;

public sealed record AdmissionTokenData(
    AdmissionId AdmissionId,
    string UserId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc);