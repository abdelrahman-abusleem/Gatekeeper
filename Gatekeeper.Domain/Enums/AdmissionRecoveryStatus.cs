namespace Gatekeeper.Domain.Enums;

public enum AdmissionRecoveryStatus
{
    Recovered,
    NotExpired,
    AlreadyResolved,
    Inconsistent
}
