using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.Abstractions;

/// <summary>Identity-fenced release of current Active admission.</summary>
public interface IAdmissionReleaser
{
    /// <summary>Atomically release only the expected current Active admission; never cancel Processing or delete a replacement.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="expectedAdmissionId">The nonempty identity of the admission the host intends to release.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>Released confirms this release. NotCurrent means a stale, repeated, absent or expired admission. Inconsistent requires investigation.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">A user identity is blank or an admission identity or claim is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="InvalidOperationException">The authority returned an unknown, empty or malformed protocol reply.</exception>
    /// <exception cref="InvalidCastException">The Redis client cannot convert the authority reply to the expected array.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>On transport failure the outcome is uncertain; retry or reconcile using the same identity condition.</remarks>
    Task<AdmissionReleaseStatus> ReleaseAsync(string userId, AdmissionId expectedAdmissionId,
        CancellationToken cancellationToken = default);
}
