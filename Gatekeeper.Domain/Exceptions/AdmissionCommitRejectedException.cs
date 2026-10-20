namespace Gatekeeper.Domain.Exceptions;

/// <summary>
/// Redis explicitly rejected this Commit invocation without applying it. This does not
/// resolve an earlier uncertain invocation; reconcile the original identity and token.
/// </summary>
public sealed class AdmissionCommitRejectedException(string message) : InvalidOperationException(message);
