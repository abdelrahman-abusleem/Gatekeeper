namespace Gatekeeper.Domain.Abstractions;

public interface IAdmissionNotifier
{
    // Wakeup only. The caller must first confirm the Redis Commit.
    Task NotifyReadyAsync(string userId, CancellationToken cancellationToken = default);
}
