using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.Models;

/// <summary>Admission claims used for signing or returned by cryptographic validation.</summary>
/// <param name="AdmissionId">The admission attempt identity.</param>
/// <param name="UserId">The signed subject identity.</param>
/// <param name="IssuedAtUtc">The issue and not-before time in UTC.</param>
/// <param name="ExpiresAtUtc">The exclusive expiry time in UTC.</param>
public sealed record AdmissionTokenData(
    AdmissionId AdmissionId,
    string UserId,
    DateTime IssuedAtUtc,
    DateTime ExpiresAtUtc);