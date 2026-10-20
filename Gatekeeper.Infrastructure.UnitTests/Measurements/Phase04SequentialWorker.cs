// Measurement-only snapshot of the Stage A sequential worker; do not use as production code.
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests.Measurements;

public sealed class Phase04SequentialWorker : BackgroundService
{
    private readonly IAdmissionClaimStore _store;
    private readonly IAdmissionTokenService _tokens;
    private readonly IAdmissionNotifier _notifier;
    private readonly QueueOptions _options;
    private readonly ILogger<Phase04SequentialWorker> _logger;
    private AdmissionId? _attemptId;
    private AdmissionClaim? _claim;
    private string? _token;
    private DateTime _expiresAtUtc;
    private bool _commitUncertain;

    public Phase04SequentialWorker(
        IAdmissionClaimStore store,
        IAdmissionTokenService tokens,
        IAdmissionNotifier notifier,
        IOptions<QueueOptions> options,
        ILogger<Phase04SequentialWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _tokens = tokens;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Exception messages from infrastructure may contain command arguments.
                _logger.LogWarning("Admission processing paused; pending attempt retained. Error type: {ErrorType}",
                    ex.GetType().Name);
            }
            try { await Task.Delay(_options.ReleaseInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    // One scheduling iteration; pending state belongs to this single worker loop.
    internal async Task ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _store.RecoverExpiredAsync(_options.BatchSize, cancellationToken);
        for (int index = 0; index < _options.BatchSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_commitUncertain)
            {
                var status = await _store.GetCommitStatusAsync(_claim!, _token!, cancellationToken);
                if (status == AdmissionCommitStatus.Committed)
                {
                    await NotifyCommittedAsync(cancellationToken);
                    continue;
                }
                if (status == AdmissionCommitStatus.Expired)
                {
                    ClearAttempt();
                    continue;
                }
                if (status != AdmissionCommitStatus.NotCommitted)
                {
                    _logger.LogWarning("Admission {AdmissionId} remains unresolved ({Status}); no new attempt started.",
                        _attemptId, status);
                    return;
                }
                // Only retry the SAME commit and token after a definitive NotCommitted observation.
            }
            else
            {
                _attemptId ??= AdmissionId.New();
                AdmissionClaimResult result;
                try
                {
                    result = await _store.ClaimNextAsync(_attemptId.Value,
                        _options.MaxActivePasses, _options.ProcessingLease, cancellationToken);
                }
                catch (AdmissionClaimUnavailableException)
                {
                    ClearAttempt();
                    continue;
                }
                if (result.Status != AdmissionClaimStatus.Claimed)
                {
                    ClearAttempt();
                    return;
                }
                _claim = result.Claim ?? throw new InvalidOperationException("Confirmed claim is missing.");
                if (_claim.AdmissionId != _attemptId.Value)
                    throw new InvalidOperationException("Confirmed claim identity differs from the pending attempt.");
                // Match JWT's second precision exactly; Redis still decides whether the lease is valid.
                var issuedAt = DateTimeOffset.FromUnixTimeSeconds(
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
                _expiresAtUtc = issuedAt.Add(_options.PassDuration);
                _token = _tokens.GenerateToken(new AdmissionTokenData(
                    _claim.AdmissionId, _claim.UserId, issuedAt, _expiresAtUtc));
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Set before sending: even a thrown/canceled await can have committed in Redis.
            _commitUncertain = true;
            try
            {
                await _store.CommitAsync(_claim!, _token!, _expiresAtUtc, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // A rejection is reconciled on the next iteration; never sign a replacement blindly.
                return;
            }
            await NotifyCommittedAsync(cancellationToken);
        }
    }

    private async Task NotifyCommittedAsync(CancellationToken cancellationToken)
    {
        var userId = _claim!.UserId;
        // Pub/Sub failure or shutdown cannot undo Commit. Polling can read the persisted token.
        ClearAttempt();
        try { await _notifier.NotifyReadyAsync(userId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Admission committed but notification failed for user {UserId}. Error type: {ErrorType}",
                userId, ex.GetType().Name);
        }
    }

    private void ClearAttempt()
    {
        _attemptId = null;
        _claim = null;
        _token = null;
        _commitUncertain = false;
    }
}
