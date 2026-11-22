using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Abstractions;

/// <summary>Stateful admission authorization against the exact current committed pass.</summary>
public interface IAdmissionValidator
{
    /// <summary>Validate cryptography, host-authorized subject, identity, expiry and the exact current Redis token.</summary>
    /// <param name="signedToken">The submitted JWT; blank or invalid tokens yield InvalidToken.</param>
    /// <param name="expectedUserId">The trusted identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>Valid alone authorizes at the operation instant. Other statuses deny admission; failures propagate.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The expected user identity is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="InvalidCastException">The Redis client cannot convert the authority reply to the expected array.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged. A failed read supplies no authoritative admission outcome.</exception>
    /// <remarks>Does not authenticate callers, cache authorization or mutate admission state. Authorization is not atomic with downstream business work.</remarks>
    Task<AdmissionValidationStatus> ValidateAdmissionAsync(string signedToken, string expectedUserId,
        CancellationToken cancellationToken = default);
}
