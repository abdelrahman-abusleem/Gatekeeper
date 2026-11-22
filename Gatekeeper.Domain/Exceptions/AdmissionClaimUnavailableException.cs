namespace Gatekeeper.Domain.Exceptions;

/// <summary>The identity cannot initiate or replay a live claim; the caller may need backoff.</summary>
/// <param name="message">The reason for the rejected or unavailable invocation.</param>
public sealed class AdmissionClaimUnavailableException(string message)
    : InvalidOperationException(message)
{
    /// <summary>Whether the Worker should back off before another identity; defaults to false for expired or resolved reservations.</summary>
    public bool RequiresBackoff { get; init; }
}
