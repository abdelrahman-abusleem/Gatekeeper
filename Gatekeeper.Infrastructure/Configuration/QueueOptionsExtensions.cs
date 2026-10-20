namespace Gatekeeper.Infrastructure.Configuration;

public static class QueueOptionsExtensions
{
    public static bool HasValidRetentionPolicy(this QueueOptions options) =>
        options.ClaimIdAcceptanceWindowSeconds is >= 1 and <= 3600
        && options.ClaimAttemptGraceSeconds is >= 1 and <= 300;

    public static bool HasValidRedisKeys(this QueueOptions options)
    {
        string[] fixedKeys = [options.QueueKey, options.ProcessingQueueKey,
            options.AdmissionRecordsKey, options.ActivePassesKey, options.QueueSequenceKey];
        string[] prefixes = [options.ClaimAttemptKeyPrefix, options.PassKeyPrefix,
            options.ActiveAdmissionKeyPrefix];
        var keys = fixedKeys.Concat(prefixes).ToArray();
        if (keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            return false;

        static string? Tag(string key)
        {
            int start = key.IndexOf('{');
            int end = start < 0 ? -1 : key.IndexOf('}', start + 1);
            return end > start + 1 ? key[(start + 1)..end] : null;
        }

        var tag = Tag(keys[0]);
        return tag is not null && keys.All(key => Tag(key) == tag)
            && !fixedKeys.Any(key => prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            && !prefixes.Any(prefix => prefixes.Any(other => prefix != other
                && prefix.StartsWith(other, StringComparison.Ordinal)));
    }

    internal static void ValidateRedisKeys(this QueueOptions options)
    {
        if (!options.HasValidRedisKeys())
            throw new Microsoft.Extensions.Options.OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName, typeof(QueueOptions),
                ["Redis keys must be disjoint and contain the same explicit nonempty Cluster hash tag."]);
    }

    public static string BuildPassKey(this QueueOptions options, string userId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return $"{options.PassKeyPrefix}{userId}";
    }

    public static string BuildNotifyChannel(this QueueOptions options, string userId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        return $"{options.NotifyChannelPrefix}{userId}";
    }
}
