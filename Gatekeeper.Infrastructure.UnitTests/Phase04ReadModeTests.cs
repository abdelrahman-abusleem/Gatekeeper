using Gatekeeper.Domain.Enums;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase04ReadModeTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData("WAITING")]
    [InlineData("PROCESSING")]
    [InlineData("ACTIVE")]
    public async Task StatusMode_NeverReadsOrTransfersJwt(string state)
    {
        var c = new Phase03Context(fixture);
        if (state == "WAITING") await c.Queue.EnqueueAsync("user");
        else if (state == "PROCESSING") await c.ClaimAsync();
        else await Phase04PollingTests.ActivateAsync(c);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "GetAdmissionSnapshot.lua"));
        // Instrument every Redis command in a test-loaded copy: any GET of the JWT fails the test.
        source = "local function checkedCall(command, ...)\n"
            + "if command == 'GET' and select(1, ...) == KEYS[5] then error('JWT bytes read in STATUS') end\n"
            + "return redis.call(command, ...) end\n" + source.Replace("redis.call(", "checkedCall(");
        var result = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source, Keys(c), ["user", "STATUS"]))!;
        Assert.Single(result);
        Assert.Equal(state, result[0].ToString());
        Assert.Equal(Enum.Parse<AdmissionStatus>(state, true), await c.Store.GetStatusAsync("user"));
    }

    [Fact]
    public async Task BothModes_ShareChecks_AndPassKeepsOriginalToken()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "GetAdmissionSnapshot.lua"));
        var status = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source, Keys(c), ["user", "STATUS"]))!;
        var pass = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source, Keys(c), ["user", "PASS"]))!;
        Assert.Single(status);
        Assert.Equal(4, pass.Length);
        Assert.Equal(status[0].ToString(), pass[0].ToString());
        Assert.Equal(token, pass[2].ToString());
        await c.Db.KeyDeleteAsync(c.TokenKey());
        foreach (var mode in new[] { "STATUS", "PASS" })
        {
            var broken = (RedisResult[])(await c.Db.ScriptEvaluateAsync(source, Keys(c), ["user", mode]))!;
            Assert.Equal("INCONSISTENT", Assert.Single(broken).ToString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("status")]
    [InlineData("UNKNOWN")]
    public async Task UnexpectedMode_IsExplicitlyRejected(string mode)
    {
        var c = new Phase03Context(fixture);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "GetAdmissionSnapshot.lua"));
        await Assert.ThrowsAsync<RedisServerException>(() => c.Db.ScriptEvaluateAsync(source, Keys(c), ["user", mode]));
    }

    private static RedisKey[] Keys(Phase03Context c) => [c.Options.QueueKey, c.Options.ProcessingQueueKey,
        c.Options.AdmissionRecordsKey, c.Options.ActivePassesKey, c.TokenKey(), c.IdentityKey()];
}
