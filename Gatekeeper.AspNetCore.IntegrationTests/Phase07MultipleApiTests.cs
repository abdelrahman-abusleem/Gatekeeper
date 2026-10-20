using System.Net;
using Gatekeeper.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Gatekeeper.AspNetCore.IntegrationTests;

public sealed class Phase07MultipleApiTests(ConsumerRedisFixture fixture) : IClassFixture<ConsumerRedisFixture>
{
    [Fact]
    public async Task IndependentApiHosts_ObserveSharedCommitReleaseAndReplacement_WithoutPositiveCache()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var secondConnection = await ConnectionMultiplexer.ConnectAsync(fixture.Connection.Configuration);
        await using var first = await ConsumerHost.StartAsync(fixture.Connection);
        await using var second = await ConsumerHost.StartAsync(secondConnection,
            admissionConfiguration: first.Services.GetRequiredService<IConfiguration>());
        var (oldId, oldToken) = await first.ActivateAsync();
        foreach (var api in new[] { first, second })
            Assert.Equal(HttpStatusCode.OK, (await api.Client.SendAsync(
                api.Request(HttpMethod.Get, "/protected", oldToken), deadline.Token)).StatusCode);

        Assert.Equal(AdmissionReleaseStatus.Released, await second.Store.ReleaseAsync("user", oldId));
        foreach (var api in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.SendAsync(
                api.Request(HttpMethod.Get, "/protected", oldToken), deadline.Token)).StatusCode);
            Assert.Equal(1, api.ProtectedExecutions);
        }
        var (newId, newToken) = await second.ActivateAsync();
        Assert.NotEqual(oldId, newId);
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await first.Store.ReleaseAsync("user", oldId));
        foreach (var api in new[] { first, second })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await api.Client.SendAsync(
                api.Request(HttpMethod.Get, "/protected", oldToken), deadline.Token)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Client.SendAsync(
                api.Request(HttpMethod.Get, "/protected", newToken), deadline.Token)).StatusCode);
            Assert.Equal(2, api.ProtectedExecutions);
            Assert.Equal(newToken, await api.Store.GetPassAsync("user"));
        }
    }
}
