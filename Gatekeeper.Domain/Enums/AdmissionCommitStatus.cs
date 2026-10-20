namespace Gatekeeper.Domain.Enums;

public enum AdmissionCommitStatus
{
    Committed,
    NotCommitted,
    Unknown,
    Mismatch,
    Inconsistent,
    Expired
}