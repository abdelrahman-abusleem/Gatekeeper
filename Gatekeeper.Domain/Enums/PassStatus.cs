namespace Gatekeeper.Domain.Enums;

/// <summary>Standalone AdmissionPass model state; the Redis runtime uses its own authoritative lifecycle.</summary>
public enum PassStatus
{
    Active,
    Expired,
    Revoked
}
