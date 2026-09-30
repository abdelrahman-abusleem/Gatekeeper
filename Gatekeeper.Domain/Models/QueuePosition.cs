namespace Gatekeeper.Domain.Models;

public sealed record QueuePosition
{
    public string UserId { get; }
    public long Position { get; }
    public TimeSpan EstimatedWaitTime { get; }

    private QueuePosition(string userId, long position, TimeSpan estimatedWaitTime) {
    
      UserId = userId;
      Position = position;
      EstimatedWaitTime = estimatedWaitTime;
    }

    public static QueuePosition Create(string userId, long position, int batchSize, TimeSpan releaseInterval)
    {
        if(string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId cannot be null or empty.", nameof(userId));

        if(position < 1)
            throw new ArgumentOutOfRangeException(nameof(position), "Position in queue must be at least 1.");

        if (batchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be at least 1.");

        if (releaseInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(releaseInterval), "Release interval must be greater than zero.");

        long batchesAhead = (position - 1) / batchSize;

        var estimatedWaitTime = TimeSpan.FromTicks(batchesAhead * releaseInterval.Ticks);

        return new QueuePosition(userId, position, estimatedWaitTime);

    }
}
