using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Gatekeeper.Infrastructure.Diagnostics;

internal static class AdmissionMetrics
{
    internal const string MeterName = "Gatekeeper.Admission";
    private static readonly Meter Meter = new(MeterName, "1.0");
    internal static readonly Counter<long> Claims = Meter.CreateCounter<long>("gatekeeper.claim.attempts");
    internal static readonly Counter<long> Commits = Meter.CreateCounter<long>("gatekeeper.commit.confirmed");
    internal static readonly Counter<long> Recoveries = Meter.CreateCounter<long>("gatekeeper.recovery.resolved");
    internal static readonly Counter<long> Failures = Meter.CreateCounter<long>("gatekeeper.operation.failures");
    internal static readonly Counter<long> Backpressure = Meter.CreateCounter<long>("gatekeeper.backpressure.events");
    internal static readonly Counter<long> TokenRejected = Meter.CreateCounter<long>("gatekeeper.token.rejected");
    internal static class TokenRejectionReason
    {
        internal const string SignatureOrFormat = "signature_or_format";
        internal const string Lifetime = "lifetime";
        internal const string Claims = "claims";
        internal const string Type = "type";
        internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { SignatureOrFormat, Lifetime, Claims, Type });
    }
    internal static readonly UpDownCounter<long> InFlight = Meter.CreateUpDownCounter<long>("gatekeeper.admission.inflight");
    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gatekeeper.admission.duration", "s");
    internal static readonly Histogram<double> RedisDuration = Meter.CreateHistogram<double>("gatekeeper.redis.duration", "s");
    internal static async Task<T> RedisAsync<T>(string operation, Func<Task<T>> action)
    {
        var start = Stopwatch.GetTimestamp();
        try { return await action(); }
        finally
        {
            RedisDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds,
            new KeyValuePair<string, object?>(AdmissionMetricTags.Operation, operation));
        }
    }
}
