namespace Gatekeeper.Domain.Exceptions;

public sealed class AdmissionClaimUnavailableException(string message)
    : InvalidOperationException(message)
{
    /// <summary>Wait before attempting another claim rather than immediately trying a new identity.</summary>
    public bool RequiresBackoff { get; init; }
}
