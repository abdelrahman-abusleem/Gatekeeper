namespace Gatekeeper.Domain.Exceptions;

/// <summary>The configured Waiting limit rejected this new entry; Redis did not enqueue it.</summary>
public sealed class WaitingQueueFullException : InvalidOperationException
{
    public WaitingQueueFullException() : base("The waiting queue is full. Retry according to the host's backpressure policy.") { }
}
