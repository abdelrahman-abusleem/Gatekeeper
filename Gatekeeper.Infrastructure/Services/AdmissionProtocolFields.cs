namespace Gatekeeper.Infrastructure.Services;

internal static class AdmissionRecordFields
{
    internal const string UserId = "userId";
    internal const string AdmissionId = "admissionId";
    internal const string OriginalScore = "originalScore";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { UserId, AdmissionId, OriginalScore });
}

internal static class AdmissionNotificationPayloads
{
    internal const string Ready = "ready";
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Ready });
}
