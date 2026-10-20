using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Abstractions;

/// <summary>Composite admission authority; hosts can depend on individual read, validation, release or recovery capabilities.</summary>
public interface IAdmissionClaimStore : IAdmissionValidator, IAdmissionReader, IAdmissionReleaser, IAdmissionRecovery
{
    /// <summary>Idempotently reserve FIFO capacity for this attempt; the claim authority owns lease and observation timestamps.</summary>
    Task<AdmissionClaimResult> ClaimNextAsync(
        AdmissionId admissionId,
        int maxActivePasses,
        TimeSpan processingLease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit the prepared JWT and UTC expiry for this claim. A definitive rejection must
    /// throw AdmissionCommitRejectedException; only that exception is handled by the Worker
    /// as a rejected-Commit retry. Any other exception, including plain InvalidOperationException,
    /// fails the lane with backoff as a protocol/dependency failure. Both paths retain the
    /// pending attempt, exact JWT and expiry for reconciliation; neither starts a new identity.
    /// </summary>
    /// <exception cref="Gatekeeper.Domain.Exceptions.AdmissionCommitRejectedException">
    /// The authority explicitly rejected this invocation; earlier uncertain invocations still require reconciliation.
    /// </exception>
    Task CommitAsync(
    AdmissionClaim claim,
    string signedToken,
    DateTime expiresAtUtc,
    CancellationToken cancellationToken = default);

    /// <summary>Reconcile this claim and exact JWT against authority; lease expiry alone does not establish Commit failure.</summary>
    Task<AdmissionCommitStatus> GetCommitStatusAsync(
    AdmissionClaim claim,
    string signedToken,
    CancellationToken cancellationToken = default);
}
