namespace Gatekeeper.Infrastructure.Services;

internal static class ClaimStatuses
{
    internal const string Claimed = "CLAIMED";
    internal const string CapacityFull = "CAPACITY_FULL";
    internal const string QueueEmpty = "QUEUE_EMPTY";
    internal const string ClaimExpired = "CLAIM_EXPIRED";
    internal const string AttemptResolved = "ATTEMPT_RESOLVED";
    internal const string IdExpired = "ID_EXPIRED";
    internal const string IdTooFuture = "ID_TOO_FUTURE";
    internal const string IdUnsupported = "ID_UNSUPPORTED";
    internal const string IdInvalid = "ID_INVALID";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Claimed, CapacityFull, QueueEmpty, ClaimExpired, AttemptResolved, IdExpired, IdTooFuture, IdUnsupported, IdInvalid });
}

internal static class CommitStatuses
{
    internal const string Committed = "COMMITTED";
    internal const string Inconsistent = "INCONSISTENT";
    internal const string NotProcessing = "NOT_PROCESSING";
    internal const string LeaseExpired = "LEASE_EXPIRED";
    internal const string RecordMissing = "RECORD_MISSING";
    internal const string AdmissionMismatch = "ADMISSION_MISMATCH";
    internal const string AttemptMissing = "ATTEMPT_MISSING";
    internal const string AttemptMismatch = "ATTEMPT_MISMATCH";
    internal const string LeaseMismatch = "LEASE_MISMATCH";
    internal const string OriginalScoreMismatch = "ORIGINAL_SCORE_MISMATCH";
    internal const string InvalidPassExpiration = "INVALID_PASS_EXPIRATION";
    internal const string AlreadyActive = "ALREADY_ACTIVE";
    internal const string ActiveIdExists = "ACTIVE_ID_EXISTS";
    internal const string TokenExists = "TOKEN_EXISTS";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Committed, Inconsistent, NotProcessing, LeaseExpired, RecordMissing, AdmissionMismatch, AttemptMissing, AttemptMismatch, LeaseMismatch, OriginalScoreMismatch, InvalidPassExpiration, AlreadyActive, ActiveIdExists, TokenExists });
}

internal static class CommitOutcomeStatuses
{
    internal const string Committed = "COMMITTED";
    internal const string NotCommitted = "NOT_COMMITTED";
    internal const string Unknown = "UNKNOWN";
    internal const string Mismatch = "MISMATCH";
    internal const string Inconsistent = "INCONSISTENT";
    internal const string Expired = "EXPIRED";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Committed, NotCommitted, Unknown, Mismatch, Inconsistent, Expired });
}

internal static class ReleaseStatuses
{
    internal const string Released = "RELEASED";
    internal const string NotCurrent = "NOT_CURRENT";
    internal const string Inconsistent = "INCONSISTENT";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Released, NotCurrent, Inconsistent });
}

internal static class RecoveryStatuses
{
    internal const string Recovered = "RECOVERED";
    internal const string NotExpired = "NOT_EXPIRED";
    internal const string AlreadyResolved = "ALREADY_RESOLVED";
    internal const string Inconsistent = "INCONSISTENT";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Recovered, NotExpired, AlreadyResolved, Inconsistent });
}

internal static class EnqueueStatuses
{
    internal const string Queued = "QUEUED";
    internal const string QueueFull = "QUEUE_FULL";
    internal const string Processing = "PROCESSING";
    internal const string Active = "ACTIVE";
    internal const string RecordConflict = "RECORD_CONFLICT";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Queued, QueueFull, Processing, Active, RecordConflict });
}

internal static class SnapshotStatuses
{
    internal const string Active = "ACTIVE";
    internal const string Waiting = "WAITING";
    internal const string Processing = "PROCESSING";
    internal const string NotFound = "NOT_FOUND";
    internal const string Inconsistent = "INCONSISTENT";
    internal const string Valid = "VALID";
    internal const string Inactive = "INACTIVE";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Active, Waiting, Processing, NotFound, Inconsistent, Valid, Inactive });
}

internal static class SnapshotModes
{
    internal const string Status = "STATUS";
    internal const string Pass = "PASS";
    internal const string Validate = "VALIDATE";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Status, Pass, Validate });
}
