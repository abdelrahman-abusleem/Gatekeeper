namespace Gatekeeper.Domain.Exceptions;
/// <summary>The configured Waiting limit rejected a new entry without enqueueing.</summary>
public sealed class WaitingQueueFullException : InvalidOperationException
{
    /// <summary>Initialize the exception with the standard rejection message.</summary>
    public WaitingQueueFullException() : base("The waiting queue is full. Retry according to the host's backpressure policy.") { }
}
