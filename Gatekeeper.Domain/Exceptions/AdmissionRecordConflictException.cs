namespace Gatekeeper.Domain.Exceptions;

/// <summary>An unresolved admission record prevents a new Waiting entry.</summary>
public sealed class AdmissionRecordConflictException : InvalidOperationException
{
    public AdmissionRecordConflictException() : base("User has an unresolved admission record.") { }
}
