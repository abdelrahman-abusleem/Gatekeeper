using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Domain.Abstractions;

public interface IAdmissionReleaser
{
    /// <summary>
    /// Atomically release only the expected current admission of a host-authorized user.
    /// A repeated/stale call returns NotCurrent without modifying newer state.
    /// A transport failure is unknown, never a successful release; re-evaluate the same identity condition.
    /// </summary>
    Task<AdmissionReleaseStatus> ReleaseAsync(string userId, AdmissionId expectedAdmissionId,
        CancellationToken cancellationToken = default);
}
