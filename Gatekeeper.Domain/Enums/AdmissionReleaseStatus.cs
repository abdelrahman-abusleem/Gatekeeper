namespace Gatekeeper.Domain.Enums;

/// <summary>Outcome of an identity-fenced Active release.</summary>
public enum AdmissionReleaseStatus
{
    /// <summary>The expected admission is absent, expired, stale or already released; newer state is untouched.</summary>
    NotCurrent = 0,
    /// <summary>The expected live admission was released and its token and identity removed.</summary>
    Released = 1,
    /// <summary>Active state or its committed attempt is malformed or conflicting; no release was applied.</summary>
    Inconsistent = 2
}
