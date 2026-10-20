using System.Net;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.AspNetCore.IntegrationTests;

public sealed class ConsumerSecurityTests(ConsumerRedisFixture fixture) : IClassFixture<ConsumerRedisFixture>
{
    [Fact]
    public async Task CompleteAuthenticatedConsumerLifecycle_EnqueuePollRetrieveProtectRelease()
    {
        await using var host = await ConsumerHost.StartAsync(fixture.Connection);
        var enqueued = await host.Client.SendAsync(host.Request(HttpMethod.Post, "/enqueue?userId=victim"));
        Assert.Equal(HttpStatusCode.OK, enqueued.StatusCode);
        Assert.Equal(AdmissionStatus.Waiting, await host.Store.GetStatusAsync("user"));
        Assert.Equal(AdmissionStatus.NotFound, await host.Store.GetStatusAsync("victim"));
        var claim = (await host.Store.ClaimNextAsync(Gatekeeper.Domain.ValueObjects.AdmissionId.New(), 50, TimeSpan.FromSeconds(30))).Claim!;
        var missingPass = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/pass"));
        Assert.Equal(HttpStatusCode.NotFound, missingPass.StatusCode);
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var token = host.Services.GetRequiredService<IAdmissionTokenService>().GenerateToken(new(claim.AdmissionId, "user", issued, issued.AddMinutes(5)));
        await host.Store.CommitAsync(claim, token, issued.AddMinutes(5));
        var options = host.Services.GetRequiredService<IOptions<QueueOptions>>().Value;
        var deadline = (long)await fixture.Connection.GetDatabase().ExecuteAsync("PEXPIRETIME", options.PassKeyPrefix + "user");
        var status = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/status?userId=victim"));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.DoesNotContain(token, await status.Content.ReadAsStringAsync());
        for (int i = 0; i < 2; i++)
        {
            var pass = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/pass?userId=victim"));
            Assert.Equal(token, await pass.Content.ReadAsStringAsync());
            Assert.True(pass.Headers.CacheControl!.NoStore);
        }
        Assert.Equal(deadline, (long)await fixture.Connection.GetDatabase().ExecuteAsync("PEXPIRETIME", options.PassKeyPrefix + "user"));
        var protectedResponse = await host.Client.SendAsync(host.Request(HttpMethod.Get, "/protected", token));
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        Assert.Equal("host:user:TestHost", await protectedResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, host.ProtectedExecutions);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(host.Request(HttpMethod.Post, "/release/" + claim.AdmissionId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/protected", token))).StatusCode);
        Assert.Equal(1, host.ProtectedExecutions);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("wrong-user")]
    [InlineData("released")]
    [InlineData("multiple")]
    [InlineData("expired")]
    [InlineData("stale")]
    public async Task ProtectedRoute_DeniesUnusableAdmission(string fault)
    {
        await using var host = await ConsumerHost.StartAsync(fixture.Connection);
        var (id, token) = await host.ActivateAsync();
        if (fault == "released") await host.Store.ReleaseAsync("user", id);
        if (fault == "expired") token = host.Services.GetRequiredService<IAdmissionTokenService>().GenerateToken(
            new(id, "user", DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1)));
        if (fault == "stale") { await host.Store.ReleaseAsync("user", id); await host.ActivateAsync(); }
        var request = host.Request(HttpMethod.Get, "/protected", fault == "missing" ? null : fault == "malformed" ? "invalid.jwt" : token,
            user: fault == "wrong-user" ? "other" : "user");
        if (fault == "multiple") request.Headers.Add("X-Gatekeeper-Admission", "second-value");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(request)).StatusCode);
        Assert.Equal(0, host.ProtectedExecutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingHostDefaultAndNamedAuthorization_CannotBeBypassed(bool defaultPolicy)
    {
        await using var host = await ConsumerHost.StartAsync(fixture.Connection);
        var (_, token) = await host.ActivateAsync();
        var request = host.Request(HttpMethod.Get, defaultPolicy ? "/admission-only" : "/protected", token,
            businessAllowed: defaultPolicy, defaultAllowed: !defaultPolicy);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(request)).StatusCode);
        Assert.Equal(0, host.ProtectedExecutions);
    }

    [Fact]
    public async Task AdmissionToken_DoesNotAuthenticateHost_AndOtherRoutesRemainNormal()
    {
        await using var host = await ConsumerHost.StartAsync(fixture.Connection);
        var (_, token) = await host.ActivateAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/protected", token, user: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/pass?userId=user", user: null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/open")).StatusCode);
        Assert.Equal(0, host.ProtectedExecutions);
    }

    [Fact]
    public async Task QueryStringAndCookie_AreNotAdmissionTransport()
    {
        await using var host = await ConsumerHost.StartAsync(fixture.Connection);
        var (_, token) = await host.ActivateAsync();
        // Deliberate negative test only; consumers must never place bearer tokens in URLs.
        var request = host.Request(HttpMethod.Get, "/protected?access_token=" + Uri.EscapeDataString(token));
        request.Headers.Add("Cookie", "admission=" + token);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(request)).StatusCode);
        Assert.Equal(0, host.ProtectedExecutions);
    }

    [Fact]
    public async Task GetPassAndRelease_UseAuthorizedUser_NotRequestUserId()
    {
        await using var host = await ConsumerHost.StartAsync(fixture.Connection);
        var (victim, token) = await host.ActivateAsync("victim");
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/pass?userId=victim", user: "attacker"))).StatusCode);
        await host.Client.SendAsync(host.Request(HttpMethod.Post, "/release/" + victim + "?userId=victim", user: "attacker"));
        Assert.Equal(AdmissionValidationStatus.Valid, await host.Store.ValidateAdmissionAsync(token, "victim"));
        Assert.Equal(token, await host.Store.GetPassAsync("victim"));
    }

    [Fact]
    public async Task ExplicitHostIdentityMappingAndHeader_AreHonored()
    {
        await using (var host = await ConsumerHost.StartAsync(fixture.Connection, nullIdentity: true))
        {
            var (_, token) = await host.ActivateAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/protected", token))).StatusCode);
        }
        await using (var host = await ConsumerHost.StartAsync(fixture.Connection, headerName: "X-Queue-Pass"))
        {
            var (_, token) = await host.ActivateAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/protected", token))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/protected", token, headerName: "X-Queue-Pass"))).StatusCode);
        }
    }

    [Fact]
    public async Task ActualRedisOutage_ProducesUnavailable_NotProtectedExecutionOrFakePollingSuccess()
    {
        await using var container = new RedisBuilder("redis:7.0").Build();
        await container.StartAsync();
        var config = ConfigurationOptions.Parse(container.GetConnectionString());
        config.AsyncTimeout = 500; config.BacklogPolicy = BacklogPolicy.FailFast;
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        await using var host = await ConsumerHost.StartAsync(connection);
        var (_, token) = await host.ActivateAsync();
        await container.StopAsync();
        foreach (var route in new[] { "/protected", "/pass", "/status" })
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.SendAsync(host.Request(HttpMethod.Get, route, token))).StatusCode);
        Assert.Equal(0, host.ProtectedExecutions);
    }
}
