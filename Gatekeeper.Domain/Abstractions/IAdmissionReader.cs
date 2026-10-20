using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Abstractions;

public interface IAdmissionReader
{
    /// <summary>Read current authoritative state. Infrastructure failures propagate; no historical expiry is inferred.</summary>
    Task<AdmissionStatus> GetStatusAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the existing, cryptographically validated current pass, or null when unavailable/inconsistent.
    /// The host must derive userId from an authenticated, authorized context. This method does not authenticate callers.
    /// Infrastructure failures propagate. Retrieval neither signs tokens nor renews admission.
    /// </summary>
    Task<string?> GetPassAsync(string userId, CancellationToken cancellationToken = default);
}
