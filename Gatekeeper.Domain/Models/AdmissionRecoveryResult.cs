using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Models;

public sealed record AdmissionRecoveryResult(string UserId, AdmissionRecoveryStatus Status);
