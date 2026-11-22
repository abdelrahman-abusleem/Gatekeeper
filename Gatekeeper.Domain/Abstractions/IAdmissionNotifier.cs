namespace Gatekeeper.Domain.Abstractions;

/// <summary>Best-effort wakeups after confirmed admission commits.</summary>
public interface IAdmissionNotifier
{
    // Wakeup only. The caller must first confirm the Redis Commit.
    /// <summary>Publish a best-effort ready wakeup after a confirmed commit.</summary>
    /// <param name="userId">The trusted user identity established and authorized by the host.</param>
    /// <param name="cancellationToken">Cancellation checked by the implementation; already dispatched Redis work is not rolled back.</param>
    /// <returns>A task completing after publication; delivery to subscribers is not guaranteed.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">The user identity is empty or whitespace.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested at a cancellation check. Cancellation does not undo work already dispatched.</exception>
    /// <exception cref="Exception">Redis connection, timeout, server and script failures propagate unchanged from the Redis implementation. A transport failure does not establish whether a mutation executed.</exception>
    /// <remarks>The payload is ready, never a JWT. Notifications only prompt a status refresh and never authorize admission.</remarks>
    Task NotifyReadyAsync(string userId, CancellationToken cancellationToken = default);
}
