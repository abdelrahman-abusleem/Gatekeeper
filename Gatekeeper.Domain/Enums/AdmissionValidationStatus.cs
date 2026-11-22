namespace Gatekeeper.Domain.Enums;

/// <summary>Stateful validation result; only Valid authorizes admission.</summary>
public enum AdmissionValidationStatus
{
    /// <summary>Cryptographic validation or required UTC token data failed.</summary>
    InvalidToken = 0,
    /// <summary>The signed subject differs from the trusted expected user.</summary>
    SubjectMismatch = 1,
    /// <summary>No matching exact current Active admission was observed.</summary>
    Inactive = 2,
    /// <summary>The Redis authority is malformed or conflicting; admission is denied.</summary>
    Inconsistent = 3,
    /// <summary>Signature, subject, identity, exact token and live expiry agree at the authority observation.</summary>
    Valid = 4
}
