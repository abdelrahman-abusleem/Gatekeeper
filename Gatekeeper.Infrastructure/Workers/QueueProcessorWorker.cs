using System.Diagnostics;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Workers;

public sealed class QueueProcessorWorker : BackgroundService
{
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(30);
    private readonly IAdmissionClaimStore _store;
    private readonly IAdmissionTokenService _tokens;
    private readonly IAdmissionNotifier _notifier;
    private readonly QueueOptions _options;
    private readonly ILogger<QueueProcessorWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly AdmissionLane[] _lanes;
    private readonly AdmissionRetryPolicy _retry;
    private readonly AdmissionRecoveryLoop _recovery;
    private readonly SemaphoreSlim _iteration = new(1, 1);
    private readonly object _claimStarts = new();
    private Task? _currentBatch;
    private int _stopClaims;
    private int _firstLane;

    private sealed class IterationBudget(int limit)
    {
        private int _used;
        public int StopNewClaims;
        public bool Take() => Interlocked.Increment(ref _used) <= limit;
    }

    private enum Outcome { Continue, Stop, Retry }

    public QueueProcessorWorker(IAdmissionClaimStore store, IAdmissionTokenService tokens,
        IAdmissionNotifier notifier, IOptions<QueueOptions> options, ILogger<QueueProcessorWorker> logger)
        : this(store, tokens, notifier, options, logger, TimeProvider.System) { }

    internal QueueProcessorWorker(IAdmissionClaimStore store, IAdmissionTokenService tokens,
        IAdmissionNotifier notifier, IOptions<QueueOptions> options, ILogger<QueueProcessorWorker> logger, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store; _tokens = tokens; _notifier = notifier; _options = options.Value; _logger = logger; _clock = clock;
        if (_options.MaxConcurrentAdmissions is < 1 or > 64 || _options.BatchSize is < 1 or > 10000
            || _options.WorkerRetryMaxDelaySeconds is < 1 or > 86400 || _options.ReleaseIntervalSeconds is < 1 or > 86400)
            throw new OptionsValidationException(Options.DefaultName, typeof(QueueOptions), ["Invalid worker scheduling limits."]);
        _retry = new AdmissionRetryPolicy(_options);
        _recovery = new AdmissionRecoveryLoop(_store, _options, _logger, _retry);
        _lanes = Enumerable.Range(0, Math.Min(_options.MaxConcurrentAdmissions, _options.BatchSize))
            .Select(_ => new AdmissionLane()).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Independent Recovery inside this hosted service; no lane owns or can block its schedule.
        var recovery = _recovery.RunAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested && Volatile.Read(ref _stopClaims) == 0)
            {
                try
                {
                    var batch = ProcessBatchAsync(stoppingToken, respectBackoff: true, recover: false);
                    Volatile.Write(ref _currentBatch, batch);
                    await batch;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("Admission processing paused; pending attempts retained. Error type: {ErrorType}", ex.GetType().Name);
                }
                finally { Volatile.Write(ref _currentBatch, null); }
                await Task.Delay(_options.ReleaseInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await recovery; }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_claimStarts) Volatile.Write(ref _stopClaims, 1);
        var batch = Volatile.Read(ref _currentBatch);
        if (batch is not null)
        {
            // Bound the drain phase to 30 seconds; the host's shutdown token can shorten it.
            try { await batch.WaitAsync(ShutdownDrainTimeout, cancellationToken); }
            catch (Exception) { /* The scheduling loop observes/reports lane failures; never log bearer data. */ }
        }
        await base.StopAsync(cancellationToken);
    }

    // A single iteration. Direct tests can drive iterations without real scheduler waits;
    // production always respects monotonic per-lane backoff.
    internal async Task ProcessBatchAsync(CancellationToken cancellationToken = default,
        bool respectBackoff = false, bool recover = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _iteration.WaitAsync(cancellationToken);
        try
        {
            if (recover) await _recovery.RecoverOnceAsync(cancellationToken);
            var budget = new IterationBudget(_options.BatchSize);
            var tasks = new Task[_lanes.Length];
            // Rotate launch order so even synchronously completing custom stores cannot starve a lane.
            var first = _firstLane;
            _firstLane = (_firstLane + 1) % _lanes.Length;
            for (int index = 0; index < _lanes.Length; index++)
                tasks[index] = RunLaneAsync(_lanes[(first + index) % _lanes.Length], budget, respectBackoff, cancellationToken);
            await Task.WhenAll(tasks);
        }
        finally { _iteration.Release(); }
    }

    private async Task RunLaneAsync(AdmissionLane lane, IterationBudget budget, bool respectBackoff, CancellationToken cancellationToken)
    {
        if (respectBackoff && lane.IsBackingOff(_clock)) return;
        while (budget.Take())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lane.AttemptId is null && (Volatile.Read(ref budget.StopNewClaims) != 0 || Volatile.Read(ref _stopClaims) != 0)) return;
            var started = Stopwatch.GetTimestamp();
            AdmissionMetrics.InFlight.Add(1);
            try
            {
                var outcome = await ProcessAttemptAsync(lane, budget, cancellationToken);
                if (outcome == Outcome.Retry) lane.RegisterFailure(_retry, _clock);
                if (outcome != Outcome.Continue) return;
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) lane.RegisterFailure(_retry, _clock);
                AdmissionMetrics.Failures.Add(1, new KeyValuePair<string, object?>(AdmissionMetricTags.Category,
                    FailureCategory(ex)));
                // No replacement identity. WhenAll observes every lane before propagating failure.
                throw;
            }
            finally
            {
                AdmissionMetrics.InFlight.Add(-1);
                AdmissionMetrics.Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }
    }

    private static string FailureCategory(Exception ex) => ex switch
    {
        OperationCanceledException => AdmissionMetricCategories.Canceled,
        TimeoutException or RedisTimeoutException => AdmissionMetricCategories.Timeout,
        RedisConnectionException => AdmissionMetricCategories.RedisUnavailable,
        ArgumentException => AdmissionMetricCategories.InvalidInput,
        InvalidOperationException => AdmissionMetricCategories.ProtocolOrDependency,
        _ => AdmissionMetricCategories.Dependency
    };

    private async Task<Outcome> ProcessAttemptAsync(AdmissionLane lane, IterationBudget budget, CancellationToken cancellationToken)
    {
        if (lane.CommitUncertain)
            return await ReconcilePendingCommitAsync(lane, cancellationToken);

        AdmissionClaimResult result;
        try
        {
            Task<AdmissionClaimResult> claim;
            // This short local gate linearizes shutdown against dispatch of NEW claims only.
            // It never reserves capacity; the Redis Claim script remains the capacity authority.
            lock (_claimStarts)
            {
                if (lane.AttemptId is null && Volatile.Read(ref _stopClaims) != 0) return Outcome.Stop;
                var attemptId = lane.EnsureAttemptId();
                AdmissionMetrics.Claims.Add(1);
                claim = _store.ClaimNextAsync(attemptId,
                    _options.MaxActivePasses, _options.ProcessingLease, cancellationToken);
            }
            result = await claim;
        }
        catch (AdmissionClaimUnavailableException) { lane.Clear(); return Outcome.Continue; }
        if (result.Status != AdmissionClaimStatus.Claimed)
        {
            AdmissionMetrics.Backpressure.Add(1, new KeyValuePair<string, object?>(AdmissionMetricTags.Reason,
                result.Status == AdmissionClaimStatus.CapacityFull ? AdmissionMetricReasons.CapacityFull : AdmissionMetricReasons.QueueEmpty));
            lane.Clear();
            Interlocked.Exchange(ref budget.StopNewClaims, 1);
            return Outcome.Stop;
        }
        lane.PrepareToken(result, _tokens, _clock, _options.PassDuration);
        return await CommitPreparedTokenAsync(lane, cancellationToken);
    }

    private async Task<Outcome> ReconcilePendingCommitAsync(AdmissionLane lane, CancellationToken cancellationToken)
    {
        var status = await _store.GetCommitStatusAsync(lane.Claim!, lane.Token!, cancellationToken);
        if (status == AdmissionCommitStatus.Committed)
        {
            AdmissionMetrics.Commits.Add(1);
            await NotifyCommittedAsync(lane, cancellationToken);
            return Outcome.Continue;
        }
        if (status == AdmissionCommitStatus.Expired) { lane.Clear(); return Outcome.Continue; }
        if (status != AdmissionCommitStatus.NotCommitted)
        {
            AdmissionMetrics.Failures.Add(1, new KeyValuePair<string, object?>(AdmissionMetricTags.Category, AdmissionMetricCategories.Unresolved));
            _logger.LogWarning("Admission remains unresolved ({Status}); no new attempt started.", status);
            _logger.LogDebug("Commit remains unresolved ({Status}); AdmissionId {AdmissionId}.", status, lane.AttemptId);
            return Outcome.Retry;
        }
        return await CommitPreparedTokenAsync(lane, cancellationToken);
    }

    private async Task<Outcome> CommitPreparedTokenAsync(AdmissionLane lane, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lane.MarkCommitUncertain();
        try { await _store.CommitAsync(lane.Claim!, lane.Token!, lane.ExpiresAtUtc, cancellationToken); }
        catch (AdmissionCommitRejectedException)
        {
            _logger.LogDebug("Commit invocation rejected; AdmissionId {AdmissionId}.", lane.AttemptId);
            AdmissionMetrics.Failures.Add(1, new KeyValuePair<string, object?>(AdmissionMetricTags.Category, AdmissionMetricCategories.CommitRejected));
            return Outcome.Retry;
        }
        AdmissionMetrics.Commits.Add(1);
        await NotifyCommittedAsync(lane, cancellationToken);
        return Outcome.Continue;
    }

    private async Task NotifyCommittedAsync(AdmissionLane lane, CancellationToken cancellationToken)
    {
        var userId = lane.Claim!.UserId;
        lane.Clear();
        try { await _notifier.NotifyReadyAsync(userId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Admission committed but notification failed. Error type: {ErrorType}", ex.GetType().Name);
        }
    }
}
