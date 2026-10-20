using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;


public interface IWaitingQueue
{

    /// <summary>Atomically enqueue a new user or return the existing Waiting position; rejections consume no sequence.</summary>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionAlreadyProcessingException">A Processing reservation already exists.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionAlreadyActiveException">A live Active admission already exists.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionRecordConflictException">Unresolved admission metadata prevents enqueue.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.WaitingQueueFullException">The configured new-entry Waiting limit was reached.</exception>
    /// <exception cref="InvalidOperationException">The Redis response is unknown or malformed; infrastructure errors propagate separately.</exception>
    Task<QueuePosition> EnqueueAsync(string userId, CancellationToken cancellationToken = default);

    Task<QueuePosition?> GetPositionAsync(string userId, CancellationToken cancellationToken = default);



    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);


    /// <summary>
    /// Remove only a Waiting entry. Returns false if none exists; does not cancel a
    /// Processing lease or revoke an Active pass. Use identity-fenced Release for Active admission.
    /// </summary>
    Task<bool> RemoveAsync(string userId, CancellationToken cancellationToken = default);
}
