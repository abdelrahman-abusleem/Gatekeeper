using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Diagnostics;
using Gatekeeper.Infrastructure.Workers;
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
    public async Task UnavailableRedis_OperationsFailFast()
    {
        using var port = ReserveClosedPort();
        using var provider = CreateProvider(PortOf(port));
        var multiplexer = provider.GetRequiredService<IConnectionMultiplexer>();
        Assert.Same(multiplexer, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.False(multiplexer.IsConnected);

        var reader = provider.GetRequiredService<IAdmissionReader>();
        var queue = provider.GetRequiredService<IWaitingQueue>();
        var validator = provider.GetRequiredService<IAdmissionValidator>();
        using var worker = ActivatorUtilities.CreateInstance<QueueProcessorWorker>(provider);
        var now = DateTime.UtcNow;
        var token = provider.GetRequiredService<IAdmissionTokenService>().GenerateToken(
            new AdmissionTokenData(AdmissionId.New(), "user", now, now.AddMinutes(5)));

        var timings = new[]
        {
            await MeasureRedisFailureAsync("GetStatusAsync", () => reader.GetStatusAsync("user")),
            await MeasureRedisFailureAsync("EnqueueAsync", () => queue.EnqueueAsync("user")),
            await MeasureRedisFailureAsync("ValidateAdmissionAsync", () => validator.ValidateAdmissionAsync(token, "user")),
            await MeasureRedisFailureAsync("Worker claim iteration", () => worker.ProcessBatchAsync(recover: false))
        };

        Assert.All(timings, AssertFast);
    }

    [Fact]
    public void HostSuppliedMultiplexer_IsPreserved()
    {
        using var port = ReserveClosedPort();
        var options = ConfigurationOptions.Parse($"127.0.0.1:{PortOf(port)},abortConnect=false,connectTimeout=1000,connectRetry=0");
        options.BacklogPolicy = BacklogPolicy.Default;
        using var multiplexer = ConnectionMultiplexer.Connect(options);
        using var provider = CreateProvider(PortOf(port), supplied: multiplexer);
        Assert.Same(multiplexer, provider.GetRequiredService<IConnectionMultiplexer>());
    }

    [Fact]
    public async Task UnavailableRedis_WorkerBoundsClaimAndRecoveryAttempts()
    {
        using var port = ReserveClosedPort();
        using var provider = CreateProvider(PortOf(port), releaseInterval: 2);
        provider.GetRequiredService<IConnectionMultiplexer>();
        using var worker = ActivatorUtilities.CreateInstance<QueueProcessorWorker>(provider);
        var operations = new ConcurrentQueue<string>();
        var bothObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new AsyncLocal<bool> { Value = true };
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument, AdmissionMetrics.RedisDuration))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (!capture.Value) return;
            foreach (var tag in tags)
            {
                if (tag.Key != AdmissionMetricTags.Operation || tag.Value is not string operation) continue;
                operations.Enqueue(operation);
                if (operations.Contains(AdmissionMetricOperations.Claim) &&
                    operations.Contains(AdmissionMetricOperations.RecoveryScan))
                    bothObserved.TrySetResult();
            }
        });
        listener.Start();

        await worker.StartAsync(default);
        try
        {
            await bothObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromSeconds(3.2));
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(shutdown.Token);
        }

        var claims = operations.Count(x => x == AdmissionMetricOperations.Claim);
        var scans = operations.Count(x => x == AdmissionMetricOperations.RecoveryScan);
        output.WriteLine($"Worker attempts in bounded outage: claim={claims}, recovery scan={scans}");
        Assert.Equal(claims + scans, operations.Count);
        Assert.InRange(claims, 1, 2);
        Assert.InRange(scans, 1, 2);
    }

    [Fact]
    public async Task RedisStartsLater_TheSameRegisteredMultiplexerRecovers()
    {
        using var closedPort = ReserveClosedPort();
        var port = PortOf(closedPort);
        using var provider = CreateProvider(port);
        var multiplexer = provider.GetRequiredService<IConnectionMultiplexer>();
        Assert.False(multiplexer.IsConnected);
        var reader = provider.GetRequiredService<IAdmissionReader>();
        AssertFast(await MeasureRedisFailureAsync("Before Redis starts", () => reader.GetStatusAsync("user")));

        await using var redis = new RedisBuilder(TestRedisImage.Name).WithPortBinding(port, 6379).Build();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        closedPort.Dispose();
        await redis.StartAsync(deadline.Token);
        while (!multiplexer.IsConnected) await Task.Delay(50, deadline.Token);

        Assert.Same(multiplexer, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.Equal(AdmissionStatus.NotFound, await reader.GetStatusAsync("user", deadline.Token));
        await provider.GetRequiredService<IWaitingQueue>().EnqueueAsync("user", deadline.Token);
        Assert.Equal(AdmissionStatus.Waiting, await reader.GetStatusAsync("user", deadline.Token));

        await redis.StopAsync(deadline.Token);
        while (multiplexer.IsConnected) await Task.Delay(50, deadline.Token);
        AssertFast(await MeasureRedisFailureAsync("After Redis stops", () => reader.GetStatusAsync("user")));

        await redis.StartAsync(deadline.Token);
        while (!multiplexer.IsConnected) await Task.Delay(50, deadline.Token);
        Assert.Same(multiplexer, provider.GetRequiredService<IConnectionMultiplexer>());
        await provider.GetRequiredService<IWaitingQueue>().EnqueueAsync("after-restart", deadline.Token);
        Assert.Equal(AdmissionStatus.Waiting, await reader.GetStatusAsync("after-restart", deadline.Token));
    }

    private async Task<(string Operation, TimeSpan Elapsed)> MeasureRedisFailureAsync(string operation, Func<Task> action)
    {
        var started = Stopwatch.GetTimestamp();
        var error = await Assert.ThrowsAnyAsync<RedisException>(
            () => action().WaitAsync(TimeSpan.FromSeconds(10)));
        var elapsed = Stopwatch.GetElapsedTime(started);
        output.WriteLine($"{operation}: {elapsed.TotalMilliseconds:F2} ms, {error.GetType().Name}");
        return (operation, elapsed);
    }

    private static void AssertFast((string Operation, TimeSpan Elapsed) result) =>
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(2),
            $"{result.Operation} took {result.Elapsed.TotalMilliseconds:F2} ms (expected under 2000 ms).");

    private static Socket ReserveClosedPort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static ServiceProvider CreateProvider(int port, string abortOption = "",
        IConnectionMultiplexer? supplied = null, int? releaseInterval = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = $"127.0.0.1:{port},connectTimeout=1000,connectRetry=0{abortOption}",
            ["JwtSettings:Issuer"] = "test-issuer",
            ["JwtSettings:Audience"] = "test-audience",
            ["JwtSettings:SecretKey"] = Convert.ToBase64String(new byte[32])
        };
        if (releaseInterval is int seconds)
        {
            settings["QueueSettings:ReleaseIntervalSeconds"] = seconds.ToString();
            settings["QueueSettings:ProcessingLeaseSeconds"] = "1";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        if (supplied is not null) services.AddSingleton(supplied);
        services.AddGatekeeper(configuration, addWorker: false);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
