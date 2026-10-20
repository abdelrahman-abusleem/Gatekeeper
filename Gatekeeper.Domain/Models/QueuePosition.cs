namespace Gatekeeper.Domain.Models;

public sealed record QueuePosition
{
    public string UserId { get; }
    public long Position { get; }
    public TimeSpan EstimatedWaitTime { get; }

    private QueuePosition(string userId, long position, TimeSpan estimatedWaitTime)
    {
        UserId = userId;
        Position = position;
        EstimatedWaitTime = estimatedWaitTime;
    }

    public static QueuePosition Create(string userId, long position, int batchSize, TimeSpan releaseInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(releaseInterval, TimeSpan.Zero);

        long batchesAhead = (position - 1) / batchSize;
        var estimatedWaitTime = TimeSpan.FromTicks(batchesAhead * releaseInterval.Ticks);

        return new QueuePosition(userId, position, estimatedWaitTime);
    }
}