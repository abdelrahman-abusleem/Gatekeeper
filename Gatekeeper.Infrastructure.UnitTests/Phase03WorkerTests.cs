using System.Collections.Concurrent;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03WorkerTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public async Task MultipleRunningWorkers_RespectGlobalCapacity_AndNeverLoseUsers()
    {
        const int users = 64, capacity = 13;
        var c = new Phase03Context(fixture, capacity, batchSize: 4);
        await Task.WhenAll(Enumerable.Range(0, users).Select(i => c.Queue.EnqueueAsync($"user-{i}")));
        var notified = new ConcurrentDictionary<string, bool>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var notifier = new CallbackNotifier(async user =>
        {
            try
            {
                var token = (string?)await c.Db.StringGetAsync(c.TokenKey(user));
                var data = await c.Tokens.ValidateTokenAsync(token!);
                Assert.NotNull(data);
                Assert.Equal(user, data.UserId);
                Assert.Equal(data.AdmissionId.ToString(), (string?)await c.Db.StringGetAsync(c.IdentityKey(user)));
                var expiry = new DateTimeOffset(data.ExpiresAtUtc).ToUnixTimeMilliseconds();
                Assert.Equal((double)expiry, await c.Db.SortedSetScoreAsync(c.Options.ActivePassesKey, user));
                Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.TokenKey(user)));
                Assert.Equal(expiry, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.IdentityKey(user)));
                Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.ProcessingQueueKey, user));
                Assert.True((await c.Db.HashGetAsync(c.Options.AdmissionRecordsKey, user)).IsNull);
                Assert.True(notified.TryAdd(user, true));
                if (notified.Count == capacity) completed.TrySetResult();
            }
            catch (Exception error) { errors.Enqueue(error); completed.TrySetException(error); }
        });
        var workers = Enumerable.Range(0, 8).Select(_ => c.Worker(notifier)).ToArray();
        int observations = 0;
        try
        {
            await Task.WhenAll(workers.Select(worker => worker.StartAsync(CancellationToken.None)));
            while (!completed.Task.IsCompleted)
            {
                var (waiting, processing, active) = await c.CountsAsync();
                Assert.InRange(processing + active, 0, capacity);
                Assert.Equal(users, waiting + processing + active);
                observations++;
                if (observations > 1000) throw new TimeoutException("Workers did not fill capacity.");
            }
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await Task.WhenAll(workers.Select(worker => worker.StopAsync(CancellationToken.None)));
            foreach (var worker in workers) worker.Dispose();
        }
        Assert.Empty(errors);
        Assert.Equal(capacity, notified.Count);
        Assert.Equal((users - capacity, 0L, (long)capacity), await c.CountsAsync());
        var waitingUsers = await c.Db.SortedSetRangeByRankAsync(c.Options.QueueKey);
        var activeUsers = await c.Db.SortedSetRangeByRankAsync(c.Options.ActivePassesKey);
        Assert.Equal(users, waitingUsers.Concat(activeUsers).Distinct().Count());
    }

    [Fact]
    public async Task BatchLimit_IsIndependentOfCapacity()
    {
        var c = new Phase03Context(fixture, capacity: 10, batchSize: 2);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(i => c.Queue.EnqueueAsync($"user-{i}")));
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask));
        await worker.ProcessBatchAsync();
        Assert.Equal((3L, 0L, 2L), await c.CountsAsync());
        await worker.ProcessBatchAsync();
        Assert.Equal((1L, 0L, 4L), await c.CountsAsync());
    }

    [Fact]
    public async Task LostClaimResponse_RetriesSameIdentityWithoutClaimingDifferentUser()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("first");
        await c.Queue.EnqueueAsync("second");
        var faults = new FaultingClaimStore(c.Store) { LostClaimResponses = 1 };
        var notifications = new List<string>();
        using var worker = c.Worker(new CallbackNotifier(user => { notifications.Add(user); return Task.CompletedTask; }), faults);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        Assert.Empty(notifications);
        Assert.Equal((1L, 1L, 0L), await c.CountsAsync());
        await worker.ProcessBatchAsync();
        Assert.Equal(2, faults.ClaimIds.Count);
        Assert.Single(faults.ClaimIds.Distinct());
        Assert.Equal(new[] { "first" }, notifications);
        Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task RedisUnavailableBeforeClaim_PreservesPendingIdentity()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var faults = new FaultingClaimStore(c.Store) { UnavailableClaimCalls = 2 };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), faults);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        await worker.ProcessBatchAsync();
        Assert.Single(faults.ClaimIds.Distinct());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task LostCommitResponse_ThenReconciliationOutage_DoesNotResignRecommitOrNotifyEarly()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var faults = new FaultingClaimStore(c.Store) { LostCommitResponses = 1, UnavailableStatusCalls = 1 };
        var tokens = new CountingTokens(c.Tokens);
        int notified = 0;
        using var worker = c.Worker(new CallbackNotifier(_ => { notified++; return Task.CompletedTask; }), faults, tokens);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        Assert.Equal(0, notified);
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        Assert.Equal(0, notified);
        await worker.ProcessBatchAsync();
        Assert.Equal(1, notified);
        Assert.Equal(1, tokens.Generated);
        Assert.Single(faults.CommitTokens);
        Assert.Single(faults.ClaimIds);
        Assert.Equal(faults.CommitTokens[0], (string?)await c.Db.StringGetAsync(c.TokenKey()));
    }

    [Fact]
    public async Task ConfirmedNotCommitted_RetriesExactSameSignedToken()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var faults = new FaultingClaimStore(c.Store) { UnavailableCommitCalls = 1 };
        var tokens = new CountingTokens(c.Tokens);
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), faults, tokens);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        await worker.ProcessBatchAsync();
        Assert.Equal(1, tokens.Generated);
        Assert.Equal(2, faults.CommitTokens.Count);
        Assert.Single(faults.CommitTokens.Distinct());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Theory]
    [InlineData(AdmissionCommitStatus.Unknown)]
    [InlineData(AdmissionCommitStatus.Inconsistent)]
    [InlineData(AdmissionCommitStatus.Mismatch)]
    public async Task AmbiguousCommitStatus_BlocksNewClaims(AdmissionCommitStatus status)
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("first");
        await c.Queue.EnqueueAsync("second");
        var faults = new FaultingClaimStore(c.Store) { LostCommitResponses = 1, ForcedStatus = status };
        using var worker = c.Worker(new CallbackNotifier(_ => throw new Exception("Must not notify")), faults);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        await worker.ProcessBatchAsync();
        await worker.ProcessBatchAsync();
        Assert.Single(faults.ClaimIds);
        Assert.Single(faults.CommitTokens);
        Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task CanceledAfterClaim_PreservesReservationForRecoveryAndFreshWorker()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        using var shutdown = new CancellationTokenSource();
        var faults = new FaultingClaimStore(c.Store) { AfterClaim = shutdown.Cancel };
        int notifications = 0;
        var notifier = new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; });
        using (var worker = c.Worker(notifier, faults))
            await Assert.ThrowsAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(shutdown.Token));
        Assert.Empty(faults.CommitTokens);
        Assert.Equal(0, notifications);
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        var claim = (await c.Store.ClaimNextAsync(faults.ClaimIds[0], 10, c.Options.ProcessingLease)).Claim!;
        await c.ExpireLeaseAsync(claim);
        using var replacement = c.Worker(notifier);
        await replacement.ProcessBatchAsync();
        Assert.Equal(1, notifications);
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        Assert.NotEqual(claim.AdmissionId.ToString(), (string?)await c.Db.StringGetAsync(c.IdentityKey()));
    }

    [Fact]
    public async Task CanceledAfterCommit_LeavesActivePassForPollingAndNoRequeue()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        using var shutdown = new CancellationTokenSource();
        var faults = new FaultingClaimStore(c.Store) { AfterCommit = shutdown.Cancel };
        int notifications = 0;
        using var worker = c.Worker(new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; }), faults);
        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(shutdown.Token));
        Assert.Equal(0, notifications);
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        Assert.NotNull(await c.Tokens.ValidateTokenAsync((await c.Db.StringGetAsync(c.TokenKey())).ToString()));
        await worker.ProcessBatchAsync();
        Assert.Equal(1, notifications);
        Assert.Single(faults.CommitTokens);
    }

    [Fact]
    public async Task LostClaimThenLeaseExpiry_IsRecoveredAndOldAttemptIsNeverCommitted()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var faults = new FaultingClaimStore(c.Store) { LostClaimResponses = 1 };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), faults);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        var old = (await c.Store.ClaimNextAsync(faults.ClaimIds[0], 10, c.Options.ProcessingLease)).Claim!;
        await c.ExpireLeaseAsync(old);
        await worker.ProcessBatchAsync();
        Assert.Empty(faults.CommitTokens);
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        await worker.ProcessBatchAsync();
        Assert.NotEqual(old.AdmissionId, faults.ClaimIds.Last());
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task LeaseExpiresBeforeCommit_NoNotificationAndUserIsRecovered()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var faults = new FaultingClaimStore(c.Store) { BeforeCommit = async claim => { await c.ExpireLeaseAsync(claim); } };
        int notifications = 0;
        using var worker = c.Worker(new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; }), faults);
        await worker.ProcessBatchAsync();
        Assert.Equal(0, notifications);
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        await worker.ProcessBatchAsync();
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task NotificationFailure_DoesNotUndoCommitOrLeakTokenInLogs()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var logger = new TestLogger<QueueProcessorWorker>();
        var notifier = new CallbackNotifier(async _ =>
        {
            var token = await c.Db.StringGetAsync(c.TokenKey());
            throw new InvalidOperationException("Simulated publish failure including " + token);
        });
        using var worker = new QueueProcessorWorker(c.Store, c.Tokens, notifier, Options.Create(c.Options), logger);
        await worker.ProcessBatchAsync();
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        var stored = (await c.Db.StringGetAsync(c.TokenKey())).ToString();
        Assert.NotNull(await c.Tokens.ValidateTokenAsync(stored));
        Assert.DoesNotContain(stored, string.Join("\n", logger.Messages));
        Assert.Single(logger.Messages);
        await worker.ProcessBatchAsync();
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Fact]
    public async Task RealPubSub_IsOnlyWakeupAfterPersistedCommit()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var subscription = await fixture.Connection.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(c.Options.BuildNotifyChannel("user")));
        try
        {
            var notifier = new RedisAdmissionNotifier(fixture.Connection, Options.Create(c.Options));
            using var worker = c.Worker(notifier);
            await worker.ProcessBatchAsync();
            var message = await subscription.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ready", message.Message.ToString());
            Assert.NotNull(await c.Tokens.ValidateTokenAsync((await c.Db.StringGetAsync(c.TokenKey())).ToString()));
            Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
        }
        finally { await subscription.UnsubscribeAsync(); }
    }

    [Fact]
    public async Task PreCanceledWorker_DoesNotTouchQueue()
    {
        var c = new Phase03Context(fixture);
        await c.Queue.EnqueueAsync("user");
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask));
        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(shutdown.Token));
        Assert.Equal((1L, 0L, 0L), await c.CountsAsync());
    }

    [Theory]
    [InlineData(AdmissionCommitStatus.Unknown)]
    [InlineData(AdmissionCommitStatus.Mismatch)]
    [InlineData(AdmissionCommitStatus.Inconsistent)]
    public async Task ProlongedUncertainOutcome_LogsStatus_WithoutIdentityTokenOrFalseProgress(AdmissionCommitStatus status)
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("first");
        await c.Queue.EnqueueAsync("second");
        var faults = new FaultingClaimStore(c.Store) { LostCommitResponses = 1, ForcedStatus = status };
        var logger = new TestLogger<QueueProcessorWorker>();
        var tokens = new CountingTokens(c.Tokens);
        int notifications = 0;
        using var worker = new QueueProcessorWorker(faults, tokens,
            new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; }), Options.Create(c.Options), logger);
        await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
        for (int retry = 0; retry < 4; retry++) await worker.ProcessBatchAsync();
        Assert.Equal(4, logger.Messages.Count(message => message.StartsWith("Admission remains unresolved")));
        Assert.Equal(4, logger.DebugMessages.Count);
        var id = Assert.Single(faults.ClaimIds);
        var token = Assert.Single(faults.CommitTokens);
        Assert.All(logger.Messages.Where(message => message.StartsWith("Admission remains unresolved")), message =>
        {
            Assert.DoesNotContain(id.ToString(), message);
            Assert.Contains(status.ToString(), message);
            Assert.Contains("no new attempt started", message);
            Assert.DoesNotContain(token, message);
            Assert.DoesNotContain("success", message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.All(logger.DebugMessages, message => Assert.Contains(id.ToString(), message));
        Assert.Equal(0, notifications);
        Assert.Equal(1, tokens.Generated);
        Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
        Assert.Equal(token, (await c.Db.StringGetAsync(c.TokenKey("first"))).ToString());
        faults.ForcedStatus = null;
        await worker.ProcessBatchAsync();
        Assert.Equal(1, notifications);
        Assert.Single(faults.ClaimIds);
        Assert.Single(faults.CommitTokens);
    }

    private sealed class CountingTokens(IAdmissionTokenService inner) : IAdmissionTokenService
    {
        public int Generated { get; private set; }
        public string GenerateToken(AdmissionTokenData data) { Generated++; return inner.GenerateToken(data); }
        public Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default) => inner.ValidateTokenAsync(token, cancellationToken);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<string> DebugMessages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (level == LogLevel.Debug) DebugMessages.Add(formatter(state, exception));
        }
    }
}
