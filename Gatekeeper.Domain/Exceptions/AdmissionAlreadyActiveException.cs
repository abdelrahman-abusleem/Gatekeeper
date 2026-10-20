namespace Gatekeeper.Domain.Exceptions;

/// <summary>The user already has a live Active admission; no new Waiting entry was created.</summary>
public sealed class AdmissionAlreadyActiveException : InvalidOperationException
{
    public AdmissionAlreadyActiveException() : base("User already has an active admission pass.") { }
}
