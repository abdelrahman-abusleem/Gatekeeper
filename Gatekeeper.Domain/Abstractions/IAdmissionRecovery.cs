using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;

/// <summary>Safe recovery of matching expired Processing reservations.</summary>
public interface IAdmissionRecovery
{
    /// <summary>Restore the original Waiting position only for a matching expired Processing reservation.</summary>
    /// <param name="claim">The original claim, with its nonempty identity, exact positive score and UTC lease deadline.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>Recovered, NotExpired, AlreadyResolved or Inconsistent; conflicting state is left untouched.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">A user identity is blank or an admission identity or claim is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="InvalidCastException">The Redis client cannot convert the authority reply to the expected array.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>Never infer a failed commit from lease expiry alone or speculatively erase conflicting Active state.</remarks>
    Task<AdmissionRecoveryStatus> RecoverAsync(AdmissionClaim claim, CancellationToken cancellationToken = default);
    /// <summary>Inspect a bounded rotating page of expired reservations and attempt safe recovery.</summary>
    /// <param name="batchSize">Positive maximum number of expired users inspected in this page.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>One recovery outcome per inspected user; inconsistent metadata remains for investigation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The batch size is less than one.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="InvalidCastException">The Redis client cannot convert the authority reply to the expected array.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(int batchSize, CancellationToken cancellationToken = default);
}
