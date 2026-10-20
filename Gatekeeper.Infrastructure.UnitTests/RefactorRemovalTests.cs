using Gatekeeper.Domain.Enums;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorRemovalTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task Remove_OnlyRemovesWaiting_WithoutCancelingProcessingOrActive()
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("waiting");
        Assert.True(await c.Queue.RemoveAsync("waiting"));
        Assert.False(await c.Queue.RemoveAsync("waiting"));
        var claim = await c.ClaimAsync();
        Assert.False(await c.Queue.RemoveAsync("user"));
        Assert.Equal(AdmissionStatus.Processing, await c.Store.GetStatusAsync("user"));
        await c.CommitAsync(claim);
        Assert.False(await c.Queue.RemoveAsync("user"));
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, "test-token"));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }
}
