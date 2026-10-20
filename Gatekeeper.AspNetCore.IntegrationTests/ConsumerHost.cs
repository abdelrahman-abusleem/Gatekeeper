using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.AspNetCore.IntegrationTests;

public sealed class ConsumerRedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.0").Build();
    public IConnectionMultiplexer Connection { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString() + ",allowAdmin=true");
        Assert.Equal("noeviction", Assert.Single(await Connection.GetServer(Connection.GetEndPoints()[0]).ConfigGetAsync("maxmemory-policy")).Value);
    }
    public async Task DisposeAsync() { Connection.Dispose(); await _redis.DisposeAsync(); }
}

internal sealed class ConsumerHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;
    public IAdmissionClaimStore Store => Services.GetRequiredService<IAdmissionClaimStore>();
    public IWaitingQueue Queue => Services.GetRequiredService<IWaitingQueue>();
    public int ProtectedExecutions;

    private ConsumerHost(WebApplication app, HttpClient client) { _app = app; Client = client; }

    public static async Task<ConsumerHost> StartAsync(IConnectionMultiplexer redis, bool nullIdentity = false,
        string headerName = "X-Gatekeeper-Admission", IConfiguration? admissionConfiguration = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // Isolated loopback tests only; production guidance requires TLS.
        builder.Logging.ClearProviders();
        var prefix = $"phase06:{{{Guid.NewGuid():N}}}";
        var settings = new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["JwtSettings:Issuer"] = "consumer-tests",
            ["JwtSettings:Audience"] = "consumer-clients",
            ["QueueSettings:QueueKey"] = prefix + ":waiting",
            ["QueueSettings:ProcessingQueueKey"] = prefix + ":processing",
            ["QueueSettings:AdmissionRecordsKey"] = prefix + ":records",
            ["QueueSettings:ActivePassesKey"] = prefix + ":active",
            ["QueueSettings:QueueSequenceKey"] = prefix + ":sequence",
            ["QueueSettings:PassKeyPrefix"] = prefix + ":pass:",
            ["QueueSettings:ActiveAdmissionKeyPrefix"] = prefix + ":identity:",
            ["QueueSettings:ClaimAttemptKeyPrefix"] = prefix + ":attempt:"
        };
        if (admissionConfiguration is not null)
            foreach (var key in settings.Keys.ToArray()) settings[key] = admissionConfiguration[key];
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton(redis);
        builder.Services.AddInfrastructure(builder.Configuration, addWorker: false);
        builder.Services.AddAuthentication("TestHost").AddScheme<AuthenticationSchemeOptions, TestHostAuthentication>("TestHost", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim("default_allowed", "yes").Build();
            options.AddPolicy("Business", policy => policy.RequireClaim("business_allowed", "yes"));
        });
        builder.Services.AddGatekeeperAdmission(options =>
        {
            options.HeaderName = headerName;
            options.UserIdResolver = principal => nullIdentity ? null : principal.FindFirst("host_id")?.Value;
        });
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            try { await next(http); }
            catch (RedisException) when (!http.Response.HasStarted)
            { http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        ConsumerHost host = null!;
        app.MapGet("/open", () => "open");
        app.MapGet("/protected", (HttpContext http) =>
        {
            Interlocked.Increment(ref host.ProtectedExecutions);
            return "host:" + TrustedUser(http) + ":" + http.User.Identity!.AuthenticationType;
        }).RequireAuthorization("Business").RequireGatekeeperAdmission();
        app.MapGet("/admission-only", () => "accepted").RequireGatekeeperAdmission();
        app.MapPost("/enqueue", async (HttpContext http, IWaitingQueue queue) =>
        {
            try { return Results.Json(await queue.EnqueueAsync(TrustedUser(http), http.RequestAborted)); }
            catch (WaitingQueueFullException) { return Results.StatusCode(429); }
        }).RequireAuthorization();
        app.MapGet("/status", async (HttpContext http, IAdmissionClaimStore store) =>
            Results.Json(new { Status = await store.GetStatusAsync(TrustedUser(http), http.RequestAborted) })).RequireAuthorization();
        app.MapGet("/pass", async (HttpContext http, IAdmissionClaimStore store) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var token = await store.GetPassAsync(TrustedUser(http), http.RequestAborted);
            return token is null ? Results.NotFound() : Results.Text(token, "application/jwt");
        }).RequireAuthorization();
        app.MapPost("/release/{id:guid}", async (Guid id, HttpContext http, IAdmissionClaimStore store) =>
            Results.Json(await store.ReleaseAsync(TrustedUser(http), new AdmissionId(id), http.RequestAborted))).RequireAuthorization();
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        host = new ConsumerHost(app, new HttpClient { BaseAddress = new Uri(address) });
        Assert.Empty(app.Services.GetServices<IHostedService>().OfType<QueueProcessorWorker>());
        return host;
    }

    public async Task<(AdmissionId Id, string Token)> ActivateAsync(string user = "user")
    {
        await Queue.EnqueueAsync(user);
        var claim = (await Store.ClaimNextAsync(AdmissionId.New(), 50, TimeSpan.FromSeconds(30))).Claim!;
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var token = Services.GetRequiredService<IAdmissionTokenService>().GenerateToken(new(claim.AdmissionId, user, issued, issued.AddMinutes(5)));
        await Store.CommitAsync(claim, token, issued.AddMinutes(5));
        return (claim.AdmissionId, token);
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string? token = null, string? user = "user",
        bool businessAllowed = true, bool defaultAllowed = true, string headerName = "X-Gatekeeper-Admission")
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null) request.Headers.Add("X-Test-User", user);
        if (businessAllowed) request.Headers.Add("X-Test-Business", "yes");
        if (defaultAllowed) request.Headers.Add("X-Test-Default", "yes");
        if (token is not null) request.Headers.Add(headerName, token);
        return request;
    }

    private static string TrustedUser(HttpContext http) => http.User.FindFirst("host_id")!.Value;
    public async ValueTask DisposeAsync() { Client.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); }
}

// Authentication fixture only: real consumers use their existing authenticated session/scheme.
internal sealed class TestHostAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());
        Claim[] claims = [new("host_id", user), new("business_allowed", Request.Headers["X-Test-Business"].ToString()),
            new("default_allowed", Request.Headers["X-Test-Default"].ToString())];
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "TestHost")), "TestHost")));
    }
}
