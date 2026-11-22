namespace Gatekeeper.Domain.Models;

/// <summary>A one-based Waiting position and coarse per-Worker estimate.</summary>
public sealed record QueuePosition
{
    /// <summary>The user identity represented by this position.</summary>
    public string UserId { get; }
    /// <summary>The one-based Waiting rank.</summary>
    public long Position { get; }
    /// <summary>Whole batches ahead multiplied by the release interval; ignores capacity and expiry and is not a promise.</summary>
    public TimeSpan EstimatedWaitTime { get; }

    private QueuePosition(string userId, long position, TimeSpan estimatedWaitTime)
    {
        UserId = userId;
        Position = position;
        EstimatedWaitTime = estimatedWaitTime;
    }

    /// <summary>Create a Waiting position and estimate.</summary>
    /// <param name="userId">The nonblank user identity.</param>
    /// <param name="position">The positive one-based position.</param>
    /// <param name="batchSize">The positive Worker batch size.</param>
    /// <param name="releaseInterval">The positive release interval.</param>
    /// <returns>The position and coarse estimated wait.</returns>
    /// <exception cref="ArgumentNullException">The user identity is null.</exception>
    /// <exception cref="ArgumentException">The user identity is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Position or batch size is less than one, or the release interval is not positive.</exception>
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