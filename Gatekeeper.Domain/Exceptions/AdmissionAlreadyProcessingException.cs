namespace Gatekeeper.Domain.Exceptions;
/// <summary>A Processing reservation prevented a new Waiting entry.</summary>
public sealed class AdmissionAlreadyProcessingException : InvalidOperationException
{
    /// <summary>Initialize the exception with the standard rejection message.</summary>
    public AdmissionAlreadyProcessingException() : base("User already has a Processing reservation.") { }
}
