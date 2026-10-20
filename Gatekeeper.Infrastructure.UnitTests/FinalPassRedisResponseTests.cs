using Gatekeeper.Infrastructure.Services;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class FinalPassRedisResponseTests
{
    [Theory]
    [InlineData("claim")]
    [InlineData("enqueue")]
    public void CheckedEmptyAndScalarReplies_KeepDistinctMessages(string operation)
    {
        var unexpected = $"Unexpected Redis {operation} response.";
        var empty = $"Empty Redis {operation} response.";
        Assert.Equal(unexpected, Assert.Throws<InvalidOperationException>(() =>
            RedisResponse.ExpectNonEmptyArray(RedisResult.Create((RedisValue)"scalar"), unexpected, empty)).Message);
        Assert.Equal(empty, Assert.Throws<InvalidOperationException>(() =>
            RedisResponse.ExpectNonEmptyArray(RedisResult.Create(Array.Empty<RedisValue>()), unexpected, empty)).Message);
    }

    [Theory]
    [InlineData("commit", "Unknown Redis commit response.")]
    [InlineData("commit status", "Invalid Redis commit status response.")]
    public void CheckedSingleStatus_PreservesOperationSpecificShapeError(string operation, string invalid)
    {
        Assert.Equal(invalid, Assert.Throws<InvalidOperationException>(() => RedisResponse.ExpectSingleStatus(
            RedisResult.Create(Array.Empty<RedisValue>()), $"Unexpected Redis {operation} response.", invalid)).Message);
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData(null)]
    public void LegacyReadPaths_PreserveClientConversionAndNullSemantics(string? value)
    {
        var result = RedisResult.Create(value is null ? RedisValue.Null : (RedisValue)value);
        var original = Record.Exception(() =>
        {
            var parts = (RedisResult[]?)result;
            if (parts is not { Length: 1 }) throw new InvalidOperationException("Invalid admission validation response.");
        });
        var current = Record.Exception(() => RedisResponse.ExpectSingleStatus(result, "Invalid admission validation response."));
        Assert.NotNull(original);
        Assert.NotNull(current);
        Assert.Equal(original.GetType(), current.GetType());
        Assert.Equal(original.Message, current.Message);
    }
}
