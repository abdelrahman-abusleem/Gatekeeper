namespace Gatekeeper.Domain.Exceptions;
/// <summary>The authority explicitly rejected this commit invocation; earlier uncertain invocations still require reconciliation.</summary>
/// <param name="message">The reason for the rejected or unavailable invocation.</param>
public sealed class AdmissionCommitRejectedException(string message) : InvalidOperationException(message);
