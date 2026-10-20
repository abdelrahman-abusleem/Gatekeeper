using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorTimeTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task ClaimResponse_ObservesRedisUtc_WithoutChangingRetryIdentityOrLease()
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("user");
        var before = await UuidRetentionTestSupport.NowAsync(c.Db);
        var id = AdmissionId.New();
        var first = await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease);
        var retry = await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease);
        var after = await UuidRetentionTestSupport.NowAsync(c.Db);
        Assert.Equal(first.Claim, retry.Claim);
        Assert.NotNull(first.ServerTimeUtc);
        Assert.NotNull(retry.ServerTimeUtc);
        Assert.Equal(DateTimeKind.Utc, first.ServerTimeUtc.Value.Kind);
        Assert.InRange(new DateTimeOffset(first.ServerTimeUtc.Value).ToUnixTimeMilliseconds(), before, after);
        Assert.True(retry.ServerTimeUtc >= first.ServerTimeUtc);
        Assert.True(retry.ServerTimeUtc < retry.Claim!.LeaseExpiresAtUtc);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task WorkerClockSkew_DoesNotChangeRedisIssuedPassLifetime(int offsetSeconds)
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var before = await UuidRetentionTestSupport.NowAsync(c.Db);
        using var worker = new QueueProcessorWorker(c.Store, c.Tokens,
            new CallbackNotifier(_ => Task.CompletedTask), Options.Create(c.Options),
            NullLogger<QueueProcessorWorker>.Instance, new OffsetClock(offsetSeconds));
        await worker.ProcessBatchAsync();
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
        var pass = await c.Store.GetPassAsync("user");
        Assert.NotNull(pass);
        var token = await c.Tokens.ValidateTokenAsync(pass);
        Assert.NotNull(token);
        var after = await UuidRetentionTestSupport.NowAsync(c.Db);
        var issued = new DateTimeOffset(token.IssuedAtUtc).ToUnixTimeMilliseconds();
        Assert.InRange(issued, before - 999, after);
        Assert.Equal(c.Options.PassDuration, token.ExpiresAtUtc - token.IssuedAtUtc);
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(pass, "user"));
        Assert.Equal(new DateTimeOffset(token.ExpiresAtUtc).ToUnixTimeMilliseconds(),
            (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey()));
    }

    private sealed class OffsetClock(int seconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddSeconds(seconds);
    }
}
