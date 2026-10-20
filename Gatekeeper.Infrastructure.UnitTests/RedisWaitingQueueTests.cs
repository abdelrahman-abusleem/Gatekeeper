using Gatekeeper.Domain.Exceptions;

using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RedisWaitingQueueTests
    : IClassFixture<RedisClaimFixture>
{
    private readonly RedisClaimFixture _fixture;

    public RedisWaitingQueueTests(
        RedisClaimFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EnqueueAsync_ShouldAssignIncreasingRedisSequenceScores()
    {
        var (queue, database, options) = CreateContext();

        var first = await queue.EnqueueAsync("user-1");
        var second = await queue.EnqueueAsync("user-2");
        var third = await queue.EnqueueAsync("user-3");

        Assert.Equal(1, first.Position);
        Assert.Equal(2, second.Position);
        Assert.Equal(3, third.Position);

        Assert.Equal(
            1d,
            await database.SortedSetScoreAsync(
                options.QueueKey, "user-1"));

        Assert.Equal(
            2d,
            await database.SortedSetScoreAsync(
                options.QueueKey, "user-2"));

        Assert.Equal(
            3d,
            await database.SortedSetScoreAsync(
                options.QueueKey, "user-3"));

        Assert.Equal(
            3,
            await queue.GetQueueLengthAsync());

        Assert.Equal(
            3L,
            (long)await database.StringGetAsync(
                options.QueueSequenceKey));
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserAlreadyWaiting_ShouldPreserveScoreAndPosition()
    {
        var (queue, database, options) = CreateContext();

        await queue.EnqueueAsync("user-1");
        await queue.EnqueueAsync("user-2");

        var originalScore =
            await database.SortedSetScoreAsync(
                options.QueueKey, "user-1");

        var result = await queue.EnqueueAsync("user-1");

        var currentScore =
            await database.SortedSetScoreAsync(
                options.QueueKey, "user-1");

        Assert.Equal(1, result.Position);
        Assert.Equal(originalScore, currentScore);

        Assert.Equal(
            2,
            await queue.GetQueueLengthAsync());

        Assert.Equal(
            2L,
            (long)await database.StringGetAsync(
                options.QueueSequenceKey));
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserProcessing_ShouldRejectWithoutChangingState()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "processing-user";

        var leaseExpiresAt = DateTimeOffset.UtcNow
            .AddSeconds(30)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ProcessingQueueKey,
            userId,
            leaseExpiresAt);

        await Assert.ThrowsAsync<AdmissionAlreadyProcessingException>(
            async () =>
            {
                await queue.EnqueueAsync(userId);
            });

        Assert.Null(
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));

        Assert.Equal(
            (double)leaseExpiresAt,
            await database.SortedSetScoreAsync(
                options.ProcessingQueueKey, userId));

        Assert.True(
            (await database.StringGetAsync(
                options.QueueSequenceKey)).IsNull);
    }

    [Fact]
    public async Task EnqueueAsync_WhenProcessingLeaseExpired_ShouldStillRejectUntilRecovery()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "expired-processing-user";

        var expiredLease = DateTimeOffset.UtcNow
            .AddMinutes(-1)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ProcessingQueueKey,
            userId,
            expiredLease);

        await Assert.ThrowsAsync<AdmissionAlreadyProcessingException>(
            async () =>
            {
                await queue.EnqueueAsync(userId);
            });

        Assert.NotNull(
            await database.SortedSetScoreAsync(
                options.ProcessingQueueKey, userId));

        Assert.Null(
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));
    }

    [Fact]
    public async Task EnqueueAsync_WhenUserActive_ShouldReject()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "active-user";

        var activeExpiresAt = DateTimeOffset.UtcNow
            .AddMinutes(5)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ActivePassesKey,
            userId,
            activeExpiresAt);

        await Assert.ThrowsAsync<AdmissionAlreadyActiveException>(
            async () =>
            {
                await queue.EnqueueAsync(userId);
            });

        Assert.Null(
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.ActivePassesKey));
    }

    [Fact]
    public async Task EnqueueAsync_WhenActivePassExpired_ShouldAllowNewQueueSession()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "expired-active-user";

        var expiredAt = DateTimeOffset.UtcNow
            .AddMinutes(-1)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ActivePassesKey,
            userId,
            expiredAt);

        var result = await queue.EnqueueAsync(userId);

        Assert.Equal(1, result.Position);

        Assert.Equal(
            1d,
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));

    }

    [Fact]
    public async Task EnqueueAsync_WhenAdmissionRecordExists_ShouldReject()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "unresolved-user";

        await database.HashSetAsync(
            options.AdmissionRecordsKey,
            userId,
            """
            {
                "userId": "unresolved-user",
                "admissionId": "00000000-0000-0000-0000-000000000001",
                "originalScore": "15"
            }
            """);

        await Assert.ThrowsAsync<AdmissionRecordConflictException>(
            async () =>
            {
                await queue.EnqueueAsync(userId);
            });

        Assert.Null(
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));

        Assert.False(
            (await database.HashGetAsync(
                options.AdmissionRecordsKey, userId)).IsNull);
    }

    [Fact]
    public async Task EnqueueAsync_WhenWaitingAndProcessingConflict_ShouldFailClosed()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "inconsistent-user";

        await database.SortedSetAddAsync(
            options.QueueKey,
            userId,
            42);

        var leaseExpiresAt = DateTimeOffset.UtcNow
            .AddMinutes(1)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ProcessingQueueKey,
            userId,
            leaseExpiresAt);

        await Assert.ThrowsAsync<AdmissionAlreadyProcessingException>(
            async () =>
            {
                await queue.EnqueueAsync(userId);
            });

        Assert.Equal(
            42d,
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));

        Assert.Equal(
            (double)leaseExpiresAt,
            await database.SortedSetScoreAsync(
                options.ProcessingQueueKey, userId));
    }

    [Fact]
    public async Task EnqueueAsync_WithConcurrentRequestsForSameUser_ShouldEnqueueOnce()
    {
        var (queue, database, options) = CreateContext();

        const string userId = "same-user";
        const int concurrentRequests = 32;

        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => queue.EnqueueAsync(userId))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(
            results,
            result => Assert.Equal(1, result.Position));

        Assert.Equal(
            1,
            await queue.GetQueueLengthAsync());

        Assert.Equal(
            1d,
            await database.SortedSetScoreAsync(
                options.QueueKey, userId));

        Assert.Equal(
            1L,
            (long)await database.StringGetAsync(
                options.QueueSequenceKey));
    }

    [Fact]
    public async Task EnqueueAsync_WithConcurrentDifferentUsers_ShouldAssignUniqueScores()
    {
        var (queue, database, options) = CreateContext();

        const int usersCount = 40;

        var tasks = Enumerable.Range(1, usersCount)
            .Select(index =>
                queue.EnqueueAsync($"user-{index}"))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(
            usersCount,
            await queue.GetQueueLengthAsync());

        var entries = await database.SortedSetRangeByRankWithScoresAsync(
            options.QueueKey);

        Assert.Equal(usersCount, entries.Length);

        var scores = entries
            .Select(entry => entry.Score)
            .OrderBy(score => score)
            .ToArray();

        var expectedScores = Enumerable.Range(1, usersCount)
            .Select(index => (double)index)
            .ToArray();

        Assert.Equal(expectedScores, scores);

        Assert.Equal(
            usersCount,
            entries
                .Select(entry => entry.Element.ToString())
                .Distinct()
                .Count());

        Assert.Equal(
            (long)usersCount,
            (long)await database.StringGetAsync(
                options.QueueSequenceKey));
    }

    private (
        RedisWaitingQueue Queue,
        IDatabase Database,
        QueueOptions Options) CreateContext()
    {

        var prefix =
            $"gatekeeper-test:{{enqueue-{Guid.NewGuid():N}}}";

        var queueOptions = new QueueOptions
        {
            ClaimAttemptKeyPrefix = $"{prefix}:claim_attempt:",
            PassKeyPrefix = $"{prefix}:pass:",
            ActiveAdmissionKeyPrefix = $"{prefix}:active_id:",
            QueueKey = $"{prefix}:waiting",
            ProcessingQueueKey = $"{prefix}:processing",
            AdmissionRecordsKey = $"{prefix}:records",
            ActivePassesKey = $"{prefix}:active",
            QueueSequenceKey = $"{prefix}:sequence"
        };

        var queue = new RedisWaitingQueue(
            _fixture.Connection,
            Options.Create(queueOptions),
            NullLogger<RedisWaitingQueue>.Instance);

        return (
            queue,
            _fixture.Connection.GetDatabase(),
            queueOptions);
    }
}
