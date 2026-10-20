namespace Gatekeeper.Domain.Exceptions;

/// <summary>The user already holds a Processing reservation; no new Waiting entry was created.</summary>
public sealed class AdmissionAlreadyProcessingException : InvalidOperationException
{
    public AdmissionAlreadyProcessingException() : base("User already has a Processing reservation.") { }
}
