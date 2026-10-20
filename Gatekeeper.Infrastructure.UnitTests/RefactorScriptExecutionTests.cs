using System.Globalization;
using System.Text.RegularExpressions;
using Gatekeeper.Domain.Enums;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorScriptExecutionTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task WarmValidation_UsesEvalSha_AndRecoversFromOutOfBandScriptFlush()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        var before = (await c.Db.ExecuteAsync("INFO", "commandstats")).ToString();
        for (int i = 0; i < 32; i++)
            Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        var warm = (await c.Db.ExecuteAsync("INFO", "commandstats")).ToString();
        Assert.Equal(32, Calls(warm, "evalsha") - Calls(before, "evalsha"));
        Assert.Equal(Calls(before, "eval"), Calls(warm, "eval"));
        Assert.Equal(Calls(before, "script|load"), Calls(warm, "script|load"));

        // A second connection flushes the server without clearing the original client's
        // endpoint cache, forcing an actual NOSCRIPT rather than a proactive local cache reset.
        using var external = await ConnectionMultiplexer.ConnectAsync(fixture.Connection.Configuration);
        await external.GetDatabase().ExecuteAsync("SCRIPT", "FLUSH");
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        Assert.Contains("errorstat_NOSCRIPT:", (await c.Db.ExecuteAsync("INFO", "errorstats")).ToString());
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
    }

    private static long Calls(string info, string command)
    {
        var match = Regex.Match(info, @"(?m)^cmdstat_" + Regex.Escape(command) + @":calls=(\d+)");
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }
}
