using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;

/// <summary>Cryptographic admission-token operations without Redis authorization.</summary>
public interface IAdmissionTokenService
{
    /// <summary>Sign an HS256 admission JWT using the primary key and its derived key identifier.</summary>
    /// <param name="data">Nonempty identity and subject with UTC dates and expiry after issuance.</param>
    /// <returns>The signed JWT; cryptography alone never authorizes admission.</returns>
    /// <exception cref="ArgumentNullException">Token data is null.</exception>
    /// <exception cref="ArgumentException">The identity is empty, subject blank, dates not UTC, or expiry does not follow issuance.</exception>
    /// <exception cref="Exception">Failures from the configured cryptographic signing provider propagate.</exception>
    string GenerateToken(AdmissionTokenData data);
    /// <summary>Validate JWT signature, HS256 algorithm, issuer, audience, claims and lifetime without consulting Redis.</summary>
    /// <param name="token">The JWT to validate; blank, malformed or rejected tokens yield null.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>Validated token data, or null for a rejected token. This result alone never authorizes admission.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="Exception">Unexpected failures from a custom token-validation provider propagate.</exception>
    /// <remarks>Cancellation is checked before and after validation and cannot interrupt the underlying cryptographic operation. Use IAdmissionValidator for Redis authority.</remarks>
    Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}
