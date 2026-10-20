
using System.ComponentModel.DataAnnotations;

namespace Gatekeeper.Infrastructure.Configuration;

public class QueueOptions
{
    public const string SectionName = "QueueSettings";

    [Range(1, 3600)]
    public int ClaimIdAcceptanceWindowSeconds { get; set; } = 300;

    [Range(1, 300)]
    public int ClaimAttemptGraceSeconds { get; set; } = 60;

    /// <summary>Waiting-only sorted set; Processing reservations use ProcessingQueueKey.</summary>
    public string QueueKey { get; set; }
        = "gatekeeper:{admission}:waiting";

    public string ProcessingQueueKey { get; init; }
        = "gatekeeper:{admission}:processing";

    public string AdmissionRecordsKey { get; init; }
        = "gatekeeper:{admission}:records";

    public string ActivePassesKey { get; init; }
        = "gatekeeper:{admission}:active";

    public string QueueSequenceKey { get; init; }
        = "gatekeeper:{admission}:sequence";

    public string ClaimAttemptKeyPrefix { get; init; }
        = "gatekeeper:{admission}:claim_attempt:";

    public string PassKeyPrefix { get; init; }
        = "gatekeeper:{admission}:pass:";

    public string NotifyChannelPrefix { get; init; }
        = "gatekeeper:{admission}:notify:";

    public string ActiveAdmissionKeyPrefix { get; init; }
    = "gatekeeper:{admission}:active_id:";

    [Range(1, 10000,
        ErrorMessage = "BatchSize must be greater than zero.")]
    public int BatchSize { get; set; } = 50;

    /// <summary>Concurrent admission lanes per Worker, bounded by BatchSize; not shared capacity.</summary>
    [Range(1, 64)]
    public int MaxConcurrentAdmissions { get; set; } = 1;

    /// <summary>Maximum new-entry Waiting occupancy; zero is unlimited. Recovery is exempt.</summary>
    [Range(0, int.MaxValue)]
    public int MaxWaitingUsers { get; set; } = 0;

    [Range(1, 86400)]
    public int WorkerRetryMaxDelaySeconds { get; set; } = 300;

    /// <summary>Shared Redis capacity for live Active passes plus Processing reservations.</summary>
    [Range(1, int.MaxValue,
        ErrorMessage = "MaxActivePasses must be greater than zero.")]
    public int MaxActivePasses { get; set; } = 50;

    [Range(1, 86400,
        ErrorMessage = "ReleaseIntervalSeconds must be at least 1 second.")]
    public int ReleaseIntervalSeconds { get; set; } = 60;

    public TimeSpan ReleaseInterval =>
        TimeSpan.FromSeconds(ReleaseIntervalSeconds);

    [Range(1, 3600,
        ErrorMessage = "ProcessingLeaseSeconds must be between 1 and 3600.")]
    public int ProcessingLeaseSeconds { get; set; } = 30;

    public TimeSpan ProcessingLease =>
        TimeSpan.FromSeconds(ProcessingLeaseSeconds);

    [Range(1, 1440,
        ErrorMessage = "PassDurationMinutes must be at least 1 minute.")]
    public int PassDurationMinutes { get; set; } = 5;

    public TimeSpan PassDuration =>
        TimeSpan.FromMinutes(PassDurationMinutes);

    /// <summary>Reserved diagnostic threshold; currently validated but does not control scheduling or admission.</summary>
    [Range(1, 1000,
        ErrorMessage = "LowLatencyThresholdMs must be between 1 and 1000 ms.")]
    public int LowLatencyThresholdMs { get; init; } = 10;

    /// <summary>Reserved diagnostic threshold; currently validated but does not control scheduling or admission.</summary>
    [Range(1, 5000,
        ErrorMessage = "HighLatencyThresholdMs must be between 1 and 5000 ms.")]
    public int HighLatencyThresholdMs { get; init; } = 50;
}
