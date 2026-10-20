namespace Gatekeeper.Infrastructure.Services;

internal static class RedisProtocolLimits
{
    internal const long MaxExactScore = 9007199254740991;
    internal const long MaxUnixMilliseconds = 253402300799999;
    internal const long MaxLeaseMilliseconds = 3600000;
}
