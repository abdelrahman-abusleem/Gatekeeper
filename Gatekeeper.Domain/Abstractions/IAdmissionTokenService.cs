using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;

public interface IAdmissionTokenService
{
    string GenerateToken(AdmissionTokenData data);

    /// <summary>
    /// Validate JWT signature, claims and lifetime only. This asynchronous crypto check
    /// does not authorize admission; use IAdmissionClaimStore.ValidateAdmissionAsync for Redis authority.
    /// </summary>
    /// <remarks>
    /// Cancellation is checked before and after validation. The underlying JsonWebTokenHandler does not accept
    /// a CancellationToken, so cancellation cannot interrupt the validation step itself.
    /// </remarks>
    Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
