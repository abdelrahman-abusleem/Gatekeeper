namespace Gatekeeper.Domain.Enums;

/// <summary>Result of a claim attempt.</summary>
public enum AdmissionClaimStatus
{
    /// <summary>A Processing reservation is confirmed and Claim is present.</summary>
    Claimed,
    /// <summary>This attempt definitively reserved nothing because effective capacity was full; retries replay the result.</summary>
    CapacityFull,
    /// <summary>This attempt definitively reserved nothing because Waiting was empty; retries replay the result.</summary>
    QueueEmpty
}