namespace Gatekeeper.Domain.Exceptions;
/// <summary>A live Active admission prevented a new Waiting entry.</summary>
public sealed class AdmissionAlreadyActiveException : InvalidOperationException
{
    /// <summary>Initialize the exception with the standard rejection message.</summary>
    public AdmissionAlreadyActiveException() : base("User already has an active admission pass.") { }
}
