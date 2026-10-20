namespace Gatekeeper.Infrastructure.Diagnostics;

internal static class AdmissionMetricTags
{
    internal const string Operation = "operation";
    internal const string Category = "category";
    internal const string Reason = "reason";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Operation, Category, Reason });
}

internal static class AdmissionMetricCategories
{
    internal const string Canceled = "canceled";
    internal const string Timeout = "timeout";
    internal const string RedisUnavailable = "redis_unavailable";
    internal const string InvalidInput = "invalid_input";
    internal const string ProtocolOrDependency = "protocol_or_dependency";
    internal const string Dependency = "dependency";
    internal const string Unresolved = "unresolved";
    internal const string CommitRejected = "commit_rejected";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Canceled, Timeout, RedisUnavailable, InvalidInput, ProtocolOrDependency, Dependency, Unresolved, CommitRejected });
}

internal static class AdmissionMetricReasons
{
    internal const string WaitingFull = "waiting_full";
    internal const string CapacityFull = "capacity_full";
    internal const string QueueEmpty = "queue_empty";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { WaitingFull, CapacityFull, QueueEmpty });
}

internal static class AdmissionMetricOperations
{
    internal const string Enqueue = "enqueue";
    internal const string Validate = "validate";
    internal const string Release = "release";
    internal const string PassSnapshot = "pass_snapshot";
    internal const string StatusSnapshot = "status_snapshot";
    internal const string Claim = "claim";
    internal const string Commit = "commit";
    internal const string CommitStatus = "commit_status";
    internal const string Recover = "recover";
    internal const string RecoveryScan = "recovery_scan";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Enqueue, Validate, Release, PassSnapshot, StatusSnapshot, Claim, Commit, CommitStatus, Recover, RecoveryScan });
}
