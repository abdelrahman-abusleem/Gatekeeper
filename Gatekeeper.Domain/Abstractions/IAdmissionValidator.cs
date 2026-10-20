using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Domain.Abstractions;

public interface IAdmissionValidator
{
    /// <summary>
    /// Validate crypto and the exact current committed admission for a host-authorized user.
    /// Only Valid permits admission. Infrastructure failures and cancellation propagate.
    /// Does not authenticate callers, mutate state or cache authorization.
    /// </summary>
    Task<AdmissionValidationStatus> ValidateAdmissionAsync(string signedToken, string expectedUserId,
        CancellationToken cancellationToken = default);
}
