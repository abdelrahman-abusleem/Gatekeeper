using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.UnitTests.Measurements;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Gatekeeper.Infrastructure.UnitTests;

[CollectionDefinition("Phase05 isolated measurements", DisableParallelization = true)]
public sealed class Phase05MeasurementCollection { }

[Collection("Phase05 isolated measurements")]
public sealed class Phase05OperationsTests(RedisClaimFixture fixture, ITestOutputHelper output) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task Metrics_AreBoundedAndSecretFree_AndInflightReturnsToZero()
    {
        var samples = new ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, source) =>
        { if (instrument.Meter.Name == AdmissionMetrics.MeterName) source.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => samples.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => samples.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.Start();
        var c = new Phase03Context(fixture, batchSize: 2, configure: o => o.MaxConcurrentAdmissions = 2);
        await c.Queue.EnqueueAsync("sensitive-user-one"); await c.Queue.EnqueueAsync("sensitive-user-two");
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask));
        await worker.ProcessBatchAsync();
        Assert.Equal(2, samples.Where(x => x.Name == "gatekeeper.commit.confirmed").Sum(x => x.Value));
        var flight = samples.Where(x => x.Name == "gatekeeper.admission.inflight").ToArray();
        Assert.Equal(0, flight.Sum(x => x.Value));
        double current = 0;
        foreach (var sample in flight) { current += sample.Value; Assert.InRange(current, 0, 2); }
        Assert.Contains(samples, x => x.Name == "gatekeeper.redis.duration" && x.Tags.Any(t => Equals(t.Value, "claim")));
        Assert.All(samples, sample => Assert.All(sample.Tags, tag =>
        {
            Assert.Contains(tag.Key, new[] { "operation", "category", "reason" });
            Assert.DoesNotContain("sensitive", tag.Value?.ToString() ?? "");
            Assert.True((tag.Value?.ToString()?.Length ?? 0) < 40);
        }));
    }

    [Fact]
    public async Task SuccessfulHotPaths_EmitNoPerUserInformationOrHigherLogs()
    {
        var c = new Phase03Context(fixture, batchSize: 2);
        var logs = new ConcurrentQueue<string>();
        var queue = new RedisWaitingQueue(fixture.Connection, Options.Create(c.Options), new QuietLogger<RedisWaitingQueue>(logs));
        var tokens = new JwtAdmissionTokenService(Options.Create(new JwtOptions
        {
            SecretKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()),
            Issuer = "phase03",
            Audience = "phase03-clients"
        }), new QuietLogger<JwtAdmissionTokenService>(logs));
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options), new QuietLogger<RedisAdmissionClaimStore>(logs), tokens);
        await queue.EnqueueAsync("private-user");
        using var worker = new QueueProcessorWorker(store, tokens, new CallbackNotifier(_ => Task.CompletedTask),
            Options.Create(c.Options), new QuietLogger<QueueProcessorWorker>(logs));
        await worker.ProcessBatchAsync();
        Assert.NotNull(await store.GetPassAsync("private-user"));
        Assert.Empty(logs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task MeasureSequentialAndBoundedWorker_OnSameIsolatedRedis(int injectedDelayMs)
    {
        var results = new List<object>();
        // Warm the actual implementations before the measured runs.
        await MeasureAsync("warmup-sequential", 1, 8, injectedDelayMs);
        await MeasureAsync("warmup-bounded", 4, 8, injectedDelayMs);
        results.Add(await MeasureAsync("phase04-sequential", 1, 96, injectedDelayMs));
        results.Add(await MeasureAsync("phase05-bounded", 1, 96, injectedDelayMs));
        results.Add(await MeasureAsync("phase05-bounded", 4, 96, injectedDelayMs));
        var json = JsonSerializer.Serialize(new
        {
            Environment = new
            {
                Runtime = Environment.Version.ToString(),
                Environment.ProcessorCount,
                OS = Environment.OSVersion.ToString(),
                Redis = "7.0 standalone disposable container",
                InjectedDelayMs = injectedDelayMs
            },
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true });
        // The test runner owns result storage (for example, an external TRX directory).
        // Keep measurements in captured output rather than writing into the checkout.
        output.WriteLine(json);
    }

    private async Task<object> MeasureAsync(string implementation, int lanes, int users, int delayMs)
    {
        var c = new Phase03Context(fixture, capacity: users, batchSize: users,
            configure: o => o.MaxConcurrentAdmissions = lanes);
        foreach (var i in Enumerable.Range(0, users)) await c.Queue.EnqueueAsync($"measure-{i}");
        var starts = new ConcurrentDictionary<Gatekeeper.Domain.ValueObjects.AdmissionId, long>();
        var latencies = new ConcurrentBag<double>();
        var inFlight = 0; var peak = 0;
        var observed = new ObservedClaimStore(c.Store);
        observed.BeforeClaim = async (id, ct) =>
        {
            starts[id] = Stopwatch.GetTimestamp();
            var current = Interlocked.Increment(ref inFlight);
            lock (starts) peak = Math.Max(peak, current);
            if (delayMs > 0) await Task.Delay(delayMs, ct);
        };
        observed.BeforeCommit = async (_, ct) => { if (delayMs > 0) await Task.Delay(delayMs, ct); };
        observed.AfterCommit = (claim, _) =>
        {
            latencies.Add(Stopwatch.GetElapsedTime(starts[claim.AdmissionId]).TotalMilliseconds);
            Interlocked.Decrement(ref inFlight);
            return Task.CompletedTask;
        };
        var notifier = new CallbackNotifier(_ => Task.CompletedTask);
        var redisLatencies = new ConcurrentBag<double>();
        using var redisListener = new MeterListener();
        redisListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AdmissionMetrics.MeterName && instrument.Name == "gatekeeper.redis.duration")
                listener.EnableMeasurementEvents(instrument);
        };
        redisListener.SetMeasurementEventCallback<double>((_, value, _, _) => redisLatencies.Add(value * 1000));
        redisListener.Start();
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocationBefore = GC.GetTotalAllocatedBytes(true);
        var workingSetBefore = process.WorkingSet64;
        var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var timer = Stopwatch.StartNew();
        if (implementation.Contains("sequential", StringComparison.Ordinal))
        {
            using var baseline = new Phase04SequentialWorker(observed, c.Tokens, notifier, Options.Create(c.Options),
                NullLogger<Phase04SequentialWorker>.Instance);
            await baseline.ProcessBatchAsync();
        }
        else
        {
            using var worker = c.Worker(notifier, observed);
            await worker.ProcessBatchAsync();
        }
        timer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocationBefore;
        var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        process.Refresh();
        var finalCounts = await c.CountsAsync();
        Assert.Equal((0L, 0L, (long)users), finalCounts);
        Assert.Equal(users, observed.Commits.Count);
        Assert.Equal(users, observed.Commits.Select(x => x.Claim.AdmissionId).Distinct().Count());
        Assert.Equal(0, inFlight);
        Assert.InRange(peak, 1, lanes);
        var sorted = latencies.Order().ToArray();
        var redisSorted = redisLatencies.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        double RedisPercentile(double p) => redisSorted[Math.Clamp((int)Math.Ceiling(p * redisSorted.Length) - 1, 0, redisSorted.Length - 1)];
        return new
        {
            Implementation = implementation,
            Lanes = lanes,
            Users = users,
            ConfirmedCommits = observed.Commits.Count,
            DrainMs = timer.Elapsed.TotalMilliseconds,
            ConfirmedCommitsPerSecond = users / timer.Elapsed.TotalSeconds,
            LatencyP50Ms = Percentile(.50),
            LatencyP95Ms = Percentile(.95),
            LatencyP99Ms = Percentile(.99),
            PeakInFlight = peak,
            FinalInFlight = inFlight,
            LogicalClaimCalls = observed.Ids.Count,
            LogicalCommitCalls = observed.Commits.Count,
            CpuMs = cpuMs,
            AllocatedBytes = allocated,
            LogicalRedisScriptOperations = redisSorted.Length,
            RedisOperationP50Ms = RedisPercentile(.50),
            RedisOperationP95Ms = RedisPercentile(.95),
            RedisOperationP99Ms = RedisPercentile(.99),
            WorkingSetBefore = workingSetBefore,
            WorkingSetAfter = process.WorkingSet64,
            ProcessCumulativePeakWorkingSet = process.PeakWorkingSet64,
            Gen0Collections = GC.CollectionCount(0) - gcBefore[0],
            Gen1Collections = GC.CollectionCount(1) - gcBefore[1],
            Gen2Collections = GC.CollectionCount(2) - gcBefore[2],
            Reconciliations = observed.StatusCalls,
            Recoveries = observed.RecoveredUsers,
            DuplicateAdmissionIds = observed.Commits.Count - observed.Commits.Select(x => x.Claim.AdmissionId).Distinct().Count(),
            FinalCapacityExcess = Math.Max(0, finalCounts.Active + finalCounts.Processing - users)
        };
    }

    private sealed class QuietLogger<T>(ConcurrentQueue<string> logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) logs.Enqueue(formatter(state, exception)); }
    }
}
