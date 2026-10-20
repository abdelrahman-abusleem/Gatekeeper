namespace Gatekeeper.Domain.Exceptions;

public sealed class AdmissionClaimUnavailableException(string message)
    : InvalidOperationException(message);
