namespace Gatekeeper.Domain.Exceptions;
/// <summary>An unresolved admission record prevented a new Waiting entry.</summary>
public sealed class AdmissionRecordConflictException : InvalidOperationException
{
    /// <summary>Initialize the exception with the standard rejection message.</summary>
    public AdmissionRecordConflictException() : base("User has an unresolved admission record.") { }
}
