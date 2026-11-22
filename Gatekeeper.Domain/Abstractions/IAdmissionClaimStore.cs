using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Abstractions;
/// <summary>Worker protocol combining claim and commit with narrow admission capabilities.</summary>
public interface IAdmissionClaimStore : IAdmissionValidator, IAdmissionReader, IAdmissionReleaser, IAdmissionRecovery
{
    /// <summary>Idempotently reserve FIFO capacity using one UUIDv7 attempt identity.</summary>
    /// <param name="admissionId">A nonempty UUIDv7 attempt identity; preserve it across uncertain responses.</param>
    /// <param name="maxActivePasses">Positive caller capacity; the lowest live registered capacity controls new claims.</param>
    /// <param name="processingLease">A whole number of milliseconds from 1 through 3600000.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>Claimed with a reservation and authority time, or a definitive CapacityFull or QueueEmpty result for this attempt.</returns>
    /// <exception cref="ArgumentException">The admission identity is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive, the lease is outside its whole-millisecond bounds, or a returned lease timestamp is out of range.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionClaimUnavailableException">The identity cannot initiate a claim, its lease expired, or the recorded attempt was resolved or replaced.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>Existing recorded reservations are reconciled before the acceptance-window and capacity checks. Transport failure is uncertain; retain the attempt identity.</remarks>
    Task<AdmissionClaimResult> ClaimNextAsync(
        AdmissionId admissionId,
        int maxActivePasses,
        TimeSpan processingLease,
        CancellationToken cancellationToken = default);
    /// <summary>Commit the exact prepared JWT and UTC expiry for the original reservation.</summary>
    /// <param name="claim">The original claim, with its nonempty identity, exact positive score and UTC lease deadline.</param>
    /// <param name="signedToken">The exact prepared JWT being committed or reconciled.</param>
    /// <param name="expiresAtUtc">The prepared pass expiry in UTC; it must remain unchanged across retries.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>A task completing when this invocation is confirmed committed.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The claim identity, user, exact score or UTC lease is invalid, or the prepared token is blank; commit expiry must also be UTC.</exception>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionCommitRejectedException">The authority definitively rejected this invocation without applying it; earlier uncertain invocations still require reconciliation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>The Worker retains claim, exact JWT and expiry across rejection and transport failures. Other exceptions indicate protocol or dependency failures and require backoff.</remarks>
    Task CommitAsync(
    AdmissionClaim claim,
    string signedToken,
    DateTime expiresAtUtc,
    CancellationToken cancellationToken = default);
    /// <summary>Reconcile the original claim and exact JWT against current authority.</summary>
    /// <param name="claim">The original claim, with its nonempty identity, exact positive score and UTC lease deadline.</param>
    /// <param name="signedToken">The exact prepared JWT being committed or reconciled.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>The reconciliation outcome; Unknown, Mismatch and Inconsistent do not establish success or a safe rollback.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The claim identity, user, exact score or UTC lease is invalid, or the prepared token is blank; commit expiry must also be UTC.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>Lease expiry alone does not establish commit failure. Committed confirms matching authority; NotCommitted identifies a matching live reservation; Expired fences a resolved or expired attempt.</remarks>
    Task<AdmissionCommitStatus> GetCommitStatusAsync(
    AdmissionClaim claim,
    string signedToken,
    CancellationToken cancellationToken = default);
}
