using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Services;

internal static class RedisResponse
{
    internal static RedisResult[] ExpectArray(RedisResult result, string message)
    {
        if (result.IsNull || result.Resp2Type != ResultType.Array)
            throw new InvalidOperationException(message);
        return (RedisResult[])result!;
    }

    // Some existing read/recovery paths cast before validating shape. Preserve the Redis
    // client's conversion exception for incompatible types instead of reclassifying it.
    internal static RedisResult[] CastArray(RedisResult result, string message)
        => (RedisResult[]?)result ?? throw new InvalidOperationException(message);

    internal static RedisResult[] ExpectNonEmptyArray(RedisResult result, string unexpected, string empty)
    {
        var parts = ExpectArray(result, unexpected);
        if (parts.Length == 0) throw new InvalidOperationException(empty);
        return parts;
    }

    internal static string ExpectSingleStatus(RedisResult result, string invalid)
    {
        var parts = CastArray(result, invalid);
        if (parts.Length != 1) throw new InvalidOperationException(invalid);
        return parts[0].ToString();
    }

    internal static string ExpectSingleStatus(RedisResult result, string unexpected, string invalid)
    {
        var parts = ExpectArray(result, unexpected);
        if (parts.Length != 1) throw new InvalidOperationException(invalid);
        return parts[0].ToString();
    }
}
