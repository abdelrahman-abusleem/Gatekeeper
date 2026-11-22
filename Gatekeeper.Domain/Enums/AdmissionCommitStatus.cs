namespace Gatekeeper.Domain.Enums;

/// <summary>Reconciliation outcome for an exact claim and JWT.</summary>
public enum AdmissionCommitStatus
{
    /// <summary>The exact identity, token and live expiry are committed consistently.</summary>
    Committed,
    /// <summary>A matching live Processing reservation remains without Active state at this observation.</summary>
    NotCommitted,
    /// <summary>History is absent inside the acceptance window; the outcome is uncertain.</summary>
    Unknown,
    /// <summary>Observed identity, token or expiry differs; this does not establish success or a safe rollback.</summary>
    Mismatch,
    /// <summary>Records are malformed or conflicting; leave state for investigation.</summary>
    Inconsistent,
    /// <summary>The attempt was recovered, released or expired; it cannot commit again.</summary>
    Expired
}