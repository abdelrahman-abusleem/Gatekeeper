using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;

public interface IAdmissionRecovery
{
    /// <summary>Recover only a matching expired reservation, restoring OriginalScore without assuming a failed Commit.</summary>
    Task<AdmissionRecoveryStatus> RecoverAsync(AdmissionClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Inspect a bounded batch of expired reservations; report inconsistent metadata without speculative repair.</summary>
    Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(int batchSize, CancellationToken cancellationToken = default);
}
