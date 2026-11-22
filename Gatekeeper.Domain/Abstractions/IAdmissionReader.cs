using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Abstractions;

/// <summary>Informational state and pass retrieval for host-authorized identities.</summary>
public interface IAdmissionReader
{
    /// <summary>Read a current structural Redis observation without inferring historical expiry.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>The observed state; Active alone is not authorization.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The user identity is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="InvalidCastException">The Redis client cannot convert the authority reply to the expected array.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged. A failed read supplies no authoritative admission outcome.</exception>
    /// <remarks>Poll with cancellation and backoff. Status and ready notifications are informational and never authorize admission; only <see cref="Gatekeeper.Domain.Enums.AdmissionValidationStatus.Valid"/> authorizes.</remarks>
    Task<AdmissionStatus> GetStatusAsync(string userId, CancellationToken cancellationToken = default);
    /// <summary>Retrieve the existing current pass after cryptographic validation and a second authority read; never sign or renew a pass.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>The exact persisted JWT when both observations agree and validation succeeds; otherwise null, including inconsistent state.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The user identity is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="InvalidCastException">The Redis client cannot convert the authority reply to the expected array.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged. A failed read supplies no authoritative admission outcome.</exception>
    /// <remarks>Poll with cancellation and backoff. Status and ready notifications are informational and never authorize admission; only <see cref="Gatekeeper.Domain.Enums.AdmissionValidationStatus.Valid"/> authorizes.</remarks>
    Task<string?> GetPassAsync(string userId, CancellationToken cancellationToken = default);
}
