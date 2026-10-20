using Gatekeeper.Infrastructure.Configuration;

namespace Gatekeeper.Infrastructure.Workers;

internal sealed class AdmissionRetryPolicy(QueueOptions options)
{
    internal const int MaxFailures = 17;
    internal TimeSpan Delay(int failures) => TimeSpan.FromSeconds(Math.Min(
        options.ReleaseIntervalSeconds * Math.Pow(2, Math.Max(0, failures - 1)),
        Math.Max(options.ReleaseIntervalSeconds, options.WorkerRetryMaxDelaySeconds)));
}
