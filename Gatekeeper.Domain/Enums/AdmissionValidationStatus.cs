namespace Gatekeeper.Domain.Enums;

public enum AdmissionValidationStatus
{
    InvalidToken = 0,
    SubjectMismatch = 1,
    Inactive = 2,
    Inconsistent = 3,
    Valid = 4
}
