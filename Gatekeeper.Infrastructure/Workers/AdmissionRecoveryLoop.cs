using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Gatekeeper.Infrastructure.Workers;

internal sealed class AdmissionRecoveryLoop(IAdmissionRecovery store, QueueOptions options,
    ILogger<QueueProcessorWorker> logger, AdmissionRetryPolicy retry)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await RecoverOnceAsync(cancellationToken); failures = 0; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures = Math.Min(AdmissionRetryPolicy.MaxFailures, failures + 1);
                logger.LogWarning("Admission Recovery paused. Error type: {ErrorType}", ex.GetType().Name);
            }
            var delay = NextDelay(options, retry, failures);
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    internal static TimeSpan NextDelay(QueueOptions options, AdmissionRetryPolicy retry, int failures) =>
        failures > 0
            ? retry.Delay(failures)
            : TimeSpan.FromTicks(Math.Min(options.ReleaseInterval.Ticks, options.ProcessingLease.Ticks));

    internal async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        var recovered = await store.RecoverExpiredAsync(options.BatchSize, cancellationToken);
        foreach (var result in recovered)
            if (result.Status == AdmissionRecoveryStatus.Recovered) AdmissionMetrics.Recoveries.Add(1);
    }
}
