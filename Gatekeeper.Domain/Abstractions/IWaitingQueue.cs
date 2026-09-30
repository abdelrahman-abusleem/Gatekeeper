using Gatekeeper.Domain.Models;

namespace Gatekeeper.Domain.Abstractions;


public interface IWaitingQueue
{
  
    Task<QueuePosition> EnqueueAsync(string userId, CancellationToken cancellationToken = default);

    Task<QueuePosition?> GetPositionAsync(string userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> DequeueBatchAsync(int batchSize, CancellationToken cancellationToken = default);

   
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);

  
    Task<bool> RemoveAsync(string userId, CancellationToken cancellationToken = default);
}