using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Models;

/// <summary>A recovery observation for one inspected user.</summary>
/// <param name="UserId">The inspected user identity.</param>
/// <param name="Status">The recovery outcome; inconsistent state remains untouched.</param>
public sealed record AdmissionRecoveryResult(string UserId, AdmissionRecoveryStatus Status);
