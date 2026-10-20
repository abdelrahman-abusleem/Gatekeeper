using System.Collections.Concurrent;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase05WorkerTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    public async Task FixedLanes_ProvideRealConcurrency_WithoutBacklogSizedTasks(int lanes, bool failPublish)
    {
        var c = new Phase03Context(fixture, capacity: 100, batchSize: 12,
            configure: o => o.MaxConcurrentAdmissions = lanes);
        foreach (var i in Enumerable.Range(0, 40)) await c.Queue.EnqueueAsync($"user-{i}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int current = 0, peak = 0, calls = 0;
        var notifier = new CallbackNotifier(async _ =>
        {
            var count = Interlocked.Increment(ref current);
            Interlocked.Increment(ref calls);
            lock (entered) peak = Math.Max(peak, count);
            if (count == lanes) entered.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref current);
            if (failPublish) throw new TimeoutException("Simulated slow Publish failure after gate release.");
        });
        using var worker = c.Worker(notifier);
        var batch = worker.ProcessBatchAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(lanes, Volatile.Read(ref calls));
            Assert.Equal((40L - lanes, 0L, (long)lanes), await c.CountsAsync());
        }
        finally { release.TrySetResult(); await batch; }
        Assert.Equal(lanes, peak);
        Assert.Equal(12, calls);
        Assert.Equal((28L, 0L, 12L), await c.CountsAsync());
    }

    [Theory]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(2, -1)]
    [InlineData(2, 0)]
    [InlineData(2, 3)]
    [InlineData(5, -1)]
    [InlineData(5, 0)]
    [InlineData(5, 3)]
    [InlineData(10, -1)]
    [InlineData(10, 0)]
    [InlineData(10, 3)]
    public async Task MultipleWorkerCounts_RespectCapacityAndPreserveEveryUser(int workerCount, int offset)
    {
        var capacity = offset < 0 ? 1 : workerCount * 2 + offset;
        const int users = 50;
        var c = new Phase03Context(fixture, capacity, batchSize: 10, configure: o => o.MaxConcurrentAdmissions = 2);
        await Task.WhenAll(Enumerable.Range(0, users).Select(i => c.Queue.EnqueueAsync($"user-{i}")));
        var notified = new ConcurrentDictionary<string, string>();
        var notifier = new CallbackNotifier(async user =>
        {
            var token = await c.Store.GetPassAsync(user);
            Assert.NotNull(token);
            if (!notified.TryAdd(user, token)) throw new InvalidOperationException("Duplicate admission.");
        });
        var workers = Enumerable.Range(0, workerCount).Select(_ => c.Worker(notifier)).ToArray();
        try
        {
            var work = Task.WhenAll(workers.Select(w => w.ProcessBatchAsync()));
            do
            {
                var counts = await c.CountsAsync();
                Assert.Equal(users, counts.Waiting + counts.Processing + counts.Active);
                Assert.InRange(counts.Active + counts.Processing, 0, capacity);
            } while (!work.IsCompleted);
            await work;
            Assert.Equal((users - capacity, 0L, (long)capacity), await c.CountsAsync());
            Assert.Equal(capacity, notified.Count);
            Assert.Equal(capacity, notified.Values.Distinct().Count());
        }
        finally { foreach (var worker in workers) worker.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneAmbiguousLane_DoesNotCorruptOtherLane_AndReusesIdentity(bool commitResponse)
    {
        var c = new Phase03Context(fixture, batchSize: 4, configure: o => o.MaxConcurrentAdmissions = 2);
        foreach (var user in new[] { "first", "second", "third", "fourth" }) await c.Queue.EnqueueAsync(user);
        var observed = new ObservedClaimStore(c.Store);
        var failed = 0;
        if (commitResponse) observed.AfterCommit = (_, _) => Interlocked.Exchange(ref failed, 1) == 0
            ? Task.FromException(new TimeoutException("Simulated lost Commit response.")) : Task.CompletedTask;
        else observed.AfterClaim = (_, _) => Interlocked.Exchange(ref failed, 1) == 0
            ? Task.FromException(new TimeoutException("Simulated lost Claim response.")) : Task.CompletedTask;
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        var idsBefore = observed.Ids.ToArray();
        Assert.Equal(4, idsBefore.Length);
        Assert.Equal(4, idsBefore.Distinct().Count());
        Assert.InRange((await c.CountsAsync()).Active, 3, 4);
        await worker.ProcessBatchAsync();
        Assert.Equal((0L, 0L, 4L), await c.CountsAsync());
        var committed = observed.Commits.ToArray();
        Assert.Equal(4, committed.Length);
        Assert.Equal(4, committed.Select(x => x.Claim.AdmissionId).Distinct().Count());
        Assert.Equal(4, committed.Select(x => x.Token).Distinct().Count());
        foreach (var item in committed) Assert.Equal(item.Token, await c.Store.GetPassAsync(item.Claim.UserId));
        if (!commitResponse) Assert.Contains(observed.Ids.GroupBy(x => x), group => group.Count() == 2);
    }

    [Fact]
    public async Task Backoff_GrowsCapsResets_AndRetainsIdentity_WithIndependentRecovery()
    {
        var c = new Phase03Context(fixture, batchSize: 1, configure: o => o.WorkerRetryMaxDelaySeconds = 4);
        await c.Queue.EnqueueAsync("first");
        var observed = new ObservedClaimStore(c.Store);
        var remaining = 4;
        observed.BeforeClaim = (_, _) => Interlocked.Decrement(ref remaining) >= 0
            ? Task.FromException(new TimeoutException("Simulated unavailable Redis.")) : Task.CompletedTask;
        var clock = new ManualWorkerClock();
        using var worker = new QueueProcessorWorker(observed, c.Tokens, new CallbackNotifier(_ => Task.CompletedTask),
            Options.Create(c.Options), NullLogger<QueueProcessorWorker>.Instance, clock);
        foreach (var delay in new[] { 1, 2, 4, 4 })
        {
            await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync(respectBackoff: true));
            var attempts = observed.Ids.Count;
            clock.Advance(delay - 0.01);
            await worker.ProcessBatchAsync(respectBackoff: true);
            Assert.Equal(attempts, observed.Ids.Count);
            clock.Advance(0.01);
        }
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Single(observed.Ids.Distinct());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        // Confirmed success resets the next operation to the initial delay, not the previous cap.
        await c.Queue.EnqueueAsync("second");
        remaining = 1;
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync(respectBackoff: true));
        clock.Advance(1);
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Equal((0L, 0L, 2L), await c.CountsAsync());
        Assert.True(observed.RecoveryCalls >= 11); // Recovery ran even during skipped/backed-off cycles.
    }

    [Fact]
    public async Task UnknownCommit_BackoffDoesNotDiscardToken_OrBlockHealthyLane()
    {
        var c = new Phase03Context(fixture, batchSize: 2, configure: o => o.MaxConcurrentAdmissions = 2);
        await c.Queue.EnqueueAsync("first"); await c.Queue.EnqueueAsync("second");
        var observed = new ObservedClaimStore(c.Store);
        var failures = 0;
        observed.AfterCommit = (_, _) => Interlocked.Exchange(ref failures, 1) == 0
            ? Task.FromException(new TimeoutException("Lost response.")) : Task.CompletedTask;
        var unresolved = true;
        observed.Status = (_, _) => Task.FromResult<AdmissionCommitStatus?>(unresolved ? AdmissionCommitStatus.Unknown : null);
        var clock = new ManualWorkerClock();
        using var worker = new QueueProcessorWorker(observed, c.Tokens, new CallbackNotifier(_ => Task.CompletedTask),
            Options.Create(c.Options), NullLogger<QueueProcessorWorker>.Instance, clock);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync(respectBackoff: true));
        await c.Queue.EnqueueAsync("third");
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Equal((0L, 0L, 3L), await c.CountsAsync());
        clock.Advance(1);
        await worker.ProcessBatchAsync(respectBackoff: true);
        clock.Advance(2);
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Equal(3, observed.Commits.Count);
        unresolved = false;
        clock.Advance(4);
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Equal(3, observed.Commits.Count);
        foreach (var item in observed.Commits) Assert.Equal(item.Token, await c.Store.GetPassAsync(item.Claim.UserId));
    }

    [Fact]
    public async Task GracefulStop_DrainsInFlightClaims_AndStartsNoMore()
    {
        var c = new Phase03Context(fixture, batchSize: 8, configure: o => o.MaxConcurrentAdmissions = 2);
        foreach (var i in Enumerable.Range(0, 8)) await c.Queue.EnqueueAsync($"user-{i}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ObservedClaimStore(c.Store);
        var calls = 0;
        observed.AfterClaim = async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = worker.StopAsync(deadline.Token);
        Assert.False(stop.IsCompleted);
        release.TrySetResult();
        await stop;
        Assert.Equal(2, calls);
        Assert.Equal((6L, 0L, 2L), await c.CountsAsync());
        Assert.True(worker.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public async Task RecoveryContinuesWhileSigningIsBlocked_AndRejectsStaleCommit()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var entered = new TaskCompletionSource<AdmissionClaim>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var observed = new ObservedClaimStore(c.Store);
        observed.AfterClaim = (result, _) => { entered.TrySetResult(result.Claim!); return Task.CompletedTask; };
        observed.AfterRecovery = results => { if (results.Any(x => x.Status == AdmissionRecoveryStatus.Recovered)) recovered.TrySetResult(); };
        var signer = new BlockingSigner(c.Tokens, release);
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed, signer);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var claim = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await c.ExpireLeaseAsync(claim);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
            Assert.Null(await c.Store.GetPassAsync("user"));
        }
        finally
        {
            release.Set();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(deadline.Token);
        }
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
    }

    private sealed class BlockingSigner(IAdmissionTokenService inner, ManualResetEventSlim release) : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data)
        {
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Signing gate did not open.");
            return inner.GenerateToken(data);
        }
        public Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => inner.ValidateTokenAsync(token, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDeadline_ObservesAllLanes_AndRestartPreservesRecovery(bool committed)
    {
        var c = new Phase03Context(fixture, batchSize: 2, configure: o => o.MaxConcurrentAdmissions = 2);
        await c.Queue.EnqueueAsync("first"); await c.Queue.EnqueueAsync("second");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ObservedClaimStore(c.Store);
        var claims = new ConcurrentQueue<AdmissionClaim>();
        var calls = 0;
        async Task Gate(AdmissionClaim claim, CancellationToken ct)
        {
            claims.Enqueue(claim);
            if (Interlocked.Increment(ref calls) == 2) entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // Ends only when worker cancellation is issued.
        }
        if (committed) observed.AfterCommit = Gate;
        else observed.AfterClaim = async (result, ct) => await Gate(result.Claim!, ct);
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource();
        var stop = worker.StopAsync(deadline.Token);
        deadline.Cancel();
        var stopError = await Record.ExceptionAsync(() => stop);
        Assert.True(stopError is null or OperationCanceledException);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
        if (committed)
        {
            foreach (var claim in claims) Assert.NotNull(await c.Store.GetPassAsync(claim.UserId));
        }
        else
        {
            Assert.Equal((0L, 2L, 0L), await c.CountsAsync());
            foreach (var claim in claims) await c.ExpireLeaseAsync(claim);
        }
        using var replacement = c.Worker(new CallbackNotifier(_ => Task.CompletedTask));
        await replacement.ProcessBatchAsync();
        Assert.Equal((0L, 0L, 2L), await c.CountsAsync());
        Assert.Equal(2, claims.Select(x => x.AdmissionId).Distinct().Count());
    }

    [Fact]
    public async Task AllLanesUnavailable_KeepOnlyTheirOriginalIds_WithoutRetryStorm()
    {
        var c = new Phase03Context(fixture, batchSize: 8, configure: o => o.MaxConcurrentAdmissions = 4);
        await c.Queue.EnqueueAsync("user");
        var observed = new ObservedClaimStore(c.Store)
        {
            BeforeClaim = (_, _) => Task.FromException(new TimeoutException("Simulated outage."))
        };
        var clock = new ManualWorkerClock();
        using var worker = new QueueProcessorWorker(observed, c.Tokens, new CallbackNotifier(_ => Task.CompletedTask),
            Options.Create(c.Options), NullLogger<QueueProcessorWorker>.Instance, clock);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync(respectBackoff: true));
        for (int cycle = 0; cycle < 100; cycle++) await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Equal(4, observed.Ids.Count);
        clock.Advance(1);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync(respectBackoff: true));
        Assert.Equal(8, observed.Ids.Count);
        Assert.Equal(4, observed.Ids.Distinct().Count());
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
    }

    [Fact]
    public async Task RetryCapBelowBaseInterval_DoesNotIncreaseRetryRate()
    {
        var c = new Phase03Context(fixture, batchSize: 1, configure: o =>
        { o.ReleaseIntervalSeconds = 10; o.WorkerRetryMaxDelaySeconds = 4; });
        await c.Queue.EnqueueAsync("user");
        var observed = new ObservedClaimStore(c.Store);
        var failures = 2;
        observed.BeforeClaim = (_, _) => Interlocked.Decrement(ref failures) >= 0
            ? Task.FromException(new TimeoutException("Simulated outage.")) : Task.CompletedTask;
        var clock = new ManualWorkerClock();
        using var worker = new QueueProcessorWorker(observed, c.Tokens, new CallbackNotifier(_ => Task.CompletedTask),
            Options.Create(c.Options), NullLogger<QueueProcessorWorker>.Instance, clock);
        for (int i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync(respectBackoff: true));
            clock.Advance(9.99);
            await worker.ProcessBatchAsync(respectBackoff: true);
            Assert.Equal(i + 1, observed.Ids.Count);
            clock.Advance(.01);
        }
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Single(observed.Ids.Distinct());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task SustainedBacklog_UsesFixedLanesAndBoundedBatches_OnOneMultiplexer()
    {
        const int users = 1000;
        var c = new Phase03Context(fixture, capacity: users, batchSize: 64, configure: o => o.MaxConcurrentAdmissions = 4);
        for (int start = 0; start < users; start += 50)
            await Task.WhenAll(Enumerable.Range(start, 50).Select(i => c.Queue.EnqueueAsync($"backlog-{i}")));
        var observed = new ObservedClaimStore(c.Store);
        int current = 0, peak = 0;
        observed.BeforeClaim = (_, _) =>
        {
            var count = Interlocked.Increment(ref current);
            lock (observed) peak = Math.Max(peak, count);
            return Task.CompletedTask;
        };
        observed.AfterClaim = (result, _) =>
        {
            if (result.Status != AdmissionClaimStatus.Claimed) Interlocked.Decrement(ref current);
            return Task.CompletedTask;
        };
        observed.AfterCommit = (_, _) => { Interlocked.Decrement(ref current); return Task.CompletedTask; };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        while ((await c.CountsAsync()).Active < users)
        {
            var before = observed.Commits.Count;
            await worker.ProcessBatchAsync();
            Assert.InRange(observed.Commits.Count - before, 1, 64);
            Assert.Equal(0, current);
        }
        Assert.InRange(peak, 2, 4);
        Assert.Equal(users, observed.Commits.Count);
        Assert.Equal(users, observed.Commits.Select(x => x.Claim.UserId).Distinct().Count());
        Assert.Equal((0L, 0L, (long)users), await c.CountsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedEmptyAndFullQueues_WaitForNextSchedulingInterval(bool full)
    {
        var c = new Phase03Context(fixture, capacity: 1, configure: o => o.ReleaseIntervalSeconds = 60);
        if (full) await Phase04PollingTests.ActivateAsync(c);
        var observed = new ObservedClaimStore(c.Store);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        observed.AfterClaim = (_, _) => { read.TrySetResult(); return Task.CompletedTask; };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        await worker.StartAsync(CancellationToken.None);
        await read.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Scheduling work queued after the first completed read does not advance the 60-second cadence.
        for (int i = 0; i < 100; i++) await Task.Yield();
        await worker.StopAsync(CancellationToken.None);
        Assert.Single(observed.Ids);
        Assert.True(worker.ExecuteTask!.IsCompleted);
    }
}
