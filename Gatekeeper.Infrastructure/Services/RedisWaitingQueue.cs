using System.Globalization;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Services;

public sealed class RedisWaitingQueue : IWaitingQueue
{
    private static readonly Lazy<string> EnqueueScript = RedisScriptSource.Load("EnqueueUser.lua");

    private readonly IDatabase _database;
    private readonly RedisKeys _keys;
    private readonly QueueOptions _options;
    private readonly ILogger<RedisWaitingQueue> _logger;

    public RedisWaitingQueue(
        IConnectionMultiplexer redis,
        IOptions<QueueOptions> options,
        ILogger<RedisWaitingQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _database = redis.GetDatabase();
        _options = options.Value;
        _keys = new RedisKeys(_options);
        _options.ValidateRedisKeys();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxWaitingUsers);
        _logger = logger;
    }

    public async Task<QueuePosition> EnqueueAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await AdmissionMetrics.RedisAsync(AdmissionMetricOperations.Enqueue, () => _database.ScriptEvaluateAsync(
            EnqueueScript.Value,
            // KEYS[1..5]: Waiting, sequence, Processing, Active, Records.
            // ARGV[1..2]: user, Waiting limit. Reply: QUEUED, zero-based rank; or one
            // QUEUE_FULL/PROCESSING/ACTIVE/RECORD_CONFLICT status. Invalid state raises a Redis error.
            keys: _keys.ForEnqueue(),
            values:
            [
                userId, _options.MaxWaitingUsers
            ]));

        var parts = RedisResponse.ExpectNonEmptyArray(result,
            "Unexpected Redis enqueue response.", "Empty Redis enqueue response.");

        var status = parts[0].ToString();

        switch (status)
        {
            case EnqueueStatuses.QueueFull when parts.Length == 1:
                AdmissionMetrics.Backpressure.Add(1, new KeyValuePair<string, object?>(AdmissionMetricTags.Reason, AdmissionMetricReasons.WaitingFull));
                throw new WaitingQueueFullException();
            case EnqueueStatuses.Processing when parts.Length == 1:
                throw new AdmissionAlreadyProcessingException();

            case EnqueueStatuses.Active when parts.Length == 1:
                throw new AdmissionAlreadyActiveException();

            case EnqueueStatuses.RecordConflict when parts.Length == 1:
                throw new AdmissionRecordConflictException();
        }

        if (status != EnqueueStatuses.Queued || parts.Length != 2)
        {
            throw new InvalidOperationException(
                "Unknown Redis enqueue response.");
        }

        if (!long.TryParse(
                parts[1].ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var rank) ||
            rank < 0)
        {
            throw new InvalidOperationException(
                "Redis returned an invalid queue rank.");
        }

        var queuePosition = BuildPosition(
            userId,
            rank);

        if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug(
            "Waiting entry accepted at position {Position} with estimated wait {EstimatedWait}",
            queuePosition.Position,
            queuePosition.EstimatedWaitTime);

        return queuePosition;
    }

    public async Task<QueuePosition?> GetPositionAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();

        long? rank = await _database.SortedSetRankAsync(
            _keys.Waiting,
            userId,
            Order.Ascending);

        if (rank is null)
            return null;

        return BuildPosition(userId, rank.Value);
    }

    public async Task<long> GetQueueLengthAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _database.SortedSetLengthAsync(
            _keys.Waiting);
    }

    public async Task<bool> RemoveAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();

        bool removed = await _database.SortedSetRemoveAsync(
            _keys.Waiting,
            userId);

        if (removed)
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug(
                "Waiting entry removed");
        }

        return removed;
    }

    private QueuePosition BuildPosition(
        string userId,
        long rank)
    {
        long position = rank + 1;

        return QueuePosition.Create(
            userId,
            position,
            _options.BatchSize,
            _options.ReleaseInterval);
    }
}
