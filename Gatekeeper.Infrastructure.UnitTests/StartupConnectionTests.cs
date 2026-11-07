using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit.Abstractions;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class StartupConnectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("")]
    [InlineData(",ABORTCONNECT=false")]
    public async Task UnavailableRedis_ResolvesOneCachedMultiplexerAndFailsClosed(string abortOption)
    {
        using var port = ReserveClosedPort();
        using var provider = CreateProvider(PortOf(port), abortOption);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolutions = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var started = Stopwatch.GetTimestamp();
            try
            {
                return (Connection: (IConnectionMultiplexer?)provider.GetRequiredService<IConnectionMultiplexer>(),
                    Error: (RedisConnectionException?)null, Ms: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (RedisConnectionException ex)
            {
                return (Connection: (IConnectionMultiplexer?)null, Error: (RedisConnectionException?)ex,
                    Ms: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(resolutions);
        var connections = results.Where(x => x.Connection is not null).Select(x => x.Connection)
            .Distinct(ReferenceEqualityComparer.Instance).ToArray();
        var errors = results.Count(x => x.Error is not null);
        // A failed factory resolution or a distinct returned singleton represents one Connect invocation.
        // The count is not the number of TCP packets or internal reconnects.
        var connectAttempts = errors + connections.Length;
        output.WriteLine($"abort option={abortOption}; parallel resolve ms: {string.Join(", ", results.Select(x => $"{x.Ms:F2}"))}; Connect factory attempts={connectAttempts}; failed resolutions={errors}");
        Assert.Equal(1, connectAttempts);
        Assert.Equal(0, errors);
        var multiplexer = Assert.Single(connections);

        var cachedAt = Stopwatch.GetTimestamp();
        for (int i = 0; i < 8; i++) Assert.Same(multiplexer, provider.GetRequiredService<IConnectionMultiplexer>());
        output.WriteLine($"Eight cached resolutions total ms: {Stopwatch.GetElapsedTime(cachedAt).TotalMilliseconds:F3}; additional Connect factory attempts=0");

        var reader = provider.GetRequiredService<IAdmissionReader>();
        var operationAt = Stopwatch.GetTimestamp();
        var error = await Assert.ThrowsAnyAsync<RedisException>(() => reader.GetStatusAsync("user"));
        output.WriteLine($"Outage status lookup ms: {Stopwatch.GetElapsedTime(operationAt).TotalMilliseconds:F2}; error={error.GetType().Name}");
    }

    [Fact]
    public async Task RedisStartsLater_TheSameRegisteredMultiplexerRecovers()
    {
        using var closedPort = ReserveClosedPort();
        var port = PortOf(closedPort);
        using var provider = CreateProvider(port);
        var multiplexer = provider.GetRequiredService<IConnectionMultiplexer>();
        Assert.False(multiplexer.IsConnected);

        await using var redis = new RedisBuilder(TestRedisImage.Name).WithPortBinding(port, 6379).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        closedPort.Dispose();
        await redis.StartAsync(deadline.Token);
        while (!multiplexer.IsConnected) await Task.Delay(50, deadline.Token);

        Assert.Same(multiplexer, provider.GetRequiredService<IConnectionMultiplexer>());
        var reader = provider.GetRequiredService<IAdmissionReader>();
        Assert.Equal(AdmissionStatus.NotFound, await reader.GetStatusAsync("user", deadline.Token));
        await provider.GetRequiredService<IWaitingQueue>().EnqueueAsync("user", deadline.Token);
        Assert.Equal(AdmissionStatus.Waiting, await reader.GetStatusAsync("user", deadline.Token));
    }

    private static Socket ReserveClosedPort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static ServiceProvider CreateProvider(int port, string abortOption = "")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = $"127.0.0.1:{port},connectTimeout=1000,connectRetry=0{abortOption}",
            ["JwtSettings:Issuer"] = "test-issuer",
            ["JwtSettings:Audience"] = "test-audience",
            ["JwtSettings:SecretKey"] = Convert.ToBase64String(new byte[32])
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddGatekeeper(configuration, addWorker: false);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
