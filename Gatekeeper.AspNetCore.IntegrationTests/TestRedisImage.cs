namespace Gatekeeper.AspNetCore.IntegrationTests;

internal static class TestRedisImage
{
    internal static string Name =>
        Environment.GetEnvironmentVariable("GATEKEEPER_TEST_REDIS_IMAGE") is { Length: > 0 } image
            ? image
            : "redis:7.0";
}
