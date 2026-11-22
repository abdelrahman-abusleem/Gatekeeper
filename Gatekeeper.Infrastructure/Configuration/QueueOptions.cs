
using System.ComponentModel.DataAnnotations;

namespace Gatekeeper.Infrastructure.Configuration;

/// <summary>Queue keys, capacity and Worker scheduling options validated at host startup.</summary>
public class QueueOptions
{
    /// <summary>The default configuration section name, QueueSettings.</summary>
    public const string SectionName = "QueueSettings";

    /// <summary>New UUIDv7 attempt acceptance window in seconds; default 300, valid 1..3600. Recorded retries remain reconcilable after this window.</summary>
    [Range(1, 3600)]
    public int ClaimIdAcceptanceWindowSeconds { get; set; } = 300;

    /// <summary>Future-clock skew and terminal retention grace in seconds; default 60, valid 1..300.</summary>
    [Range(1, 300)]
    public int ClaimAttemptGraceSeconds { get; set; } = 60;
    /// <summary>Waiting sorted-set key; default gatekeeper:{admission}:waiting. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string QueueKey { get; set; }
        = "gatekeeper:{admission}:waiting";

    /// <summary>Processing sorted-set key; default gatekeeper:{admission}:processing. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string ProcessingQueueKey { get; init; }
        = "gatekeeper:{admission}:processing";

    /// <summary>Reservation hash key; default gatekeeper:{admission}:records. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string AdmissionRecordsKey { get; init; }
        = "gatekeeper:{admission}:records";

    /// <summary>Active pass sorted-set key; default gatekeeper:{admission}:active. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string ActivePassesKey { get; init; }
        = "gatekeeper:{admission}:active";
    /// <summary>Lowest registered capacity string key; TTL is (2 * ReleaseIntervalSeconds + ProcessingLeaseSeconds) * 1000 milliseconds, default 150000; default gatekeeper:{admission}:capacity. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string CapacityKey { get; init; }
        = "gatekeeper:{admission}:capacity";

    /// <summary>FIFO sequence string key; default gatekeeper:{admission}:sequence. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string QueueSequenceKey { get; init; }
        = "gatekeeper:{admission}:sequence";

    /// <summary>Attempt-record string key prefix; default gatekeeper:{admission}:claim_attempt:. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string ClaimAttemptKeyPrefix { get; init; }
        = "gatekeeper:{admission}:claim_attempt:";

    /// <summary>Exact persisted JWT string key prefix; default gatekeeper:{admission}:pass:. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string PassKeyPrefix { get; init; }
        = "gatekeeper:{admission}:pass:";

    /// <summary>Literal ready-notification channel prefix; default gatekeeper:{admission}:notify:. No hash-tag or disjoint-key validation is applied to Pub/Sub channel names.</summary>
    public string NotifyChannelPrefix { get; init; }
        = "gatekeeper:{admission}:notify:";

    /// <summary>Current admission identity string key prefix; default gatekeeper:{admission}:active_id:. Must be nonblank, share the explicit queue hash tag, and be disjoint from every other fixed key and dynamic key prefix.</summary>
    public string ActiveAdmissionKeyPrefix { get; init; }
    = "gatekeeper:{admission}:active_id:";

    /// <summary>Maximum claims in one Worker batch; default 50, valid 1..10000.</summary>
    [Range(1, 10000,
        ErrorMessage = "BatchSize must be greater than zero.")]
    public int BatchSize { get; set; } = 50;
    /// <summary>Concurrent lanes per Worker, further bounded by BatchSize; default 1, valid 1..64. This is not shared admission capacity.</summary>
    [Range(1, 64)]
    public int MaxConcurrentAdmissions { get; set; } = 1;
    /// <summary>New-entry Waiting limit in users; default 0 means unlimited, valid 0..2147483647. Duplicates and Recovery are exempt.</summary>
    [Range(0, int.MaxValue)]
    public int MaxWaitingUsers { get; set; } = 0;

    /// <summary>Maximum Worker exponential retry delay in seconds; default 300, valid 1..86400.</summary>
    [Range(1, 86400)]
    public int WorkerRetryMaxDelaySeconds { get; set; } = 300;
    /// <summary>Caller capacity for live Active passes plus Processing reservations; default 50, valid 1..2147483647. Redis enforces the lowest live registered value for new claims; lowering never revokes existing reservations.</summary>
    [Range(1, int.MaxValue,
        ErrorMessage = "MaxActivePasses must be greater than zero.")]
    public int MaxActivePasses { get; set; } = 50;

    /// <summary>Healthy Worker release interval in seconds; default 60, valid 1..86400. Capacity registration TTL uses twice this value plus ProcessingLeaseSeconds.</summary>
    [Range(1, 86400,
        ErrorMessage = "ReleaseIntervalSeconds must be at least 1 second.")]
    public int ReleaseIntervalSeconds { get; set; } = 60;

    /// <summary>Release interval as a TimeSpan derived from ReleaseIntervalSeconds; default 60 seconds, valid 1..86400 seconds.</summary>
    public TimeSpan ReleaseInterval =>
        TimeSpan.FromSeconds(ReleaseIntervalSeconds);

    /// <summary>Processing reservation lease in seconds; default 30, valid 1..3600. Expired leases require Recovery.</summary>
    [Range(1, 3600,
        ErrorMessage = "ProcessingLeaseSeconds must be between 1 and 3600.")]
    public int ProcessingLeaseSeconds { get; set; } = 30;

    /// <summary>Processing lease as a TimeSpan derived from ProcessingLeaseSeconds; default 30 seconds, valid 1..3600 seconds.</summary>
    public TimeSpan ProcessingLease =>
        TimeSpan.FromSeconds(ProcessingLeaseSeconds);

    /// <summary>JWT pass duration in minutes; default 5, valid 1..1440. The Worker anchors it to the Redis claim observation.</summary>
    [Range(1, 1440,
        ErrorMessage = "PassDurationMinutes must be at least 1 minute.")]
    public int PassDurationMinutes { get; set; } = 5;

    /// <summary>Pass duration as a TimeSpan derived from PassDurationMinutes; default 5 minutes, valid 1..1440 minutes.</summary>
    public TimeSpan PassDuration =>
        TimeSpan.FromMinutes(PassDurationMinutes);

}
