namespace Gatekeeper.Domain.Enums;

/// <summary>Outcome of attempting to recover an expired reservation.</summary>
public enum AdmissionRecoveryStatus
{
    /// <summary>The matching expired reservation was restored to Waiting at its original score.</summary>
    Recovered,
    /// <summary>The matching Processing lease is still live; no recovery was applied.</summary>
    NotExpired,
    /// <summary>The attempt is resolved or superseded; this invocation did not restore it.</summary>
    AlreadyResolved,
    /// <summary>Required metadata is missing, malformed or conflicting; no speculative repair was applied.</summary>
    Inconsistent
}
