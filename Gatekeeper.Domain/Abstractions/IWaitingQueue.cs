using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;


/// <summary>Waiting-only queue operations for host-authorized identities.</summary>
public interface IWaitingQueue
{
    /// <summary>Atomically enqueue a new user or return the existing Waiting position without consuming another sequence.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>The one-based Waiting position and a coarse wait estimate.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The user identity is blank or exceeds 256 UTF-16 code units.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Position calculation encounters an invalid position, batch size or release interval.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.WaitingQueueFullException">The positive new-entry Waiting limit is reached; no entry was added.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionAlreadyActiveException">The user has a live Active admission.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionAlreadyProcessingException">The user has a Processing reservation.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionRecordConflictException">An unresolved admission record prevents enqueue.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>Poll with cancellation and backoff. Status and ready notifications are informational and never authorize admission; only <see cref="Gatekeeper.Domain.Enums.AdmissionValidationStatus.Valid"/> authorizes.</remarks>
    Task<QueuePosition> EnqueueAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Read a user's current Waiting position.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>The one-based Waiting position and estimate, or null when the user is not Waiting.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The user identity is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Position calculation encounters invalid position or queue timing options.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    Task<QueuePosition?> GetPositionAsync(string userId, CancellationToken cancellationToken = default);



    /// <summary>Count Waiting entries only, excluding Processing reservations and Active passes.</summary>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>The current number of Waiting entries.</returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);
    /// <summary>Remove only a Waiting entry; Processing and Active state are untouched.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>True if an entry was removed; false if no Waiting entry existed at the operation instant.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The user identity is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    Task<bool> RemoveAsync(string userId, CancellationToken cancellationToken = default);
}
