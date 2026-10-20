
using System.Text.Json;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RedisClaimFixture : IAsyncLifetime
{
    private readonly RedisContainer _container =
        new RedisBuilder("redis:7.0").Build();

    public IConnectionMultiplexer Connection { get; private set; }
        = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Connection = await ConnectionMultiplexer.ConnectAsync(
            _container.GetConnectionString() + ",allowAdmin=true");
        var policy = await Connection.GetServer(Connection.GetEndPoints()[0]).ConfigGetAsync("maxmemory-policy");
        Assert.Equal("noeviction", Assert.Single(policy).Value);
    }

    public async Task DisposeAsync()
    {
        Connection?.Dispose();
        await _container.DisposeAsync();
    }
}

public sealed class RedisAdmissionClaimStoreTests
    : IClassFixture<RedisClaimFixture>
{
    private readonly RedisClaimFixture _fixture;

    public RedisAdmissionClaimStoreTests(
        RedisClaimFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ClaimNextAsync_ShouldMoveUserToProcessingAndPreserveOriginalScore()
    {
        var (store, database, options) = CreateContext();

        const string userId = "user-1";
        const long originalScore = 42;

        await database.SortedSetAddAsync(
            options.QueueKey,
            userId,
            originalScore);

        var admissionId = AdmissionId.New();

        var result = await store.ClaimNextAsync(
            admissionId,
            maxActivePasses: 10,
            processingLease: TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.Claimed,
            result.Status);

        var claim = Assert.IsType<AdmissionClaim>(
            result.Claim);

        Assert.Equal(admissionId, claim.AdmissionId);
        Assert.Equal(userId, claim.UserId);
        Assert.Equal(originalScore, claim.OriginalScore);

        // User must be removed from Waiting.
        var waitingScore = await database.SortedSetScoreAsync(
            options.QueueKey,
            userId);

        Assert.Null(waitingScore);

        // User must exist in Processing.
        var processingScore = await database.SortedSetScoreAsync(
            options.ProcessingQueueKey,
            userId);

        Assert.NotNull(processingScore);

        var expectedLeaseMs = new DateTimeOffset(
            claim.LeaseExpiresAtUtc)
            .ToUnixTimeMilliseconds();

        Assert.Equal(
            (double)expectedLeaseMs,
            processingScore.Value);

        Assert.Equal(
            DateTimeKind.Utc,
            claim.LeaseExpiresAtUtc.Kind);

        var remainingLease =
            claim.LeaseExpiresAtUtc - DateTime.UtcNow;

        Assert.True(
            remainingLease > TimeSpan.Zero &&
            remainingLease <= TimeSpan.FromSeconds(31));

        // Redis must persist the Admission Record.
        var storedRecord = await database.HashGetAsync(
            options.AdmissionRecordsKey,
            userId);

        Assert.False(storedRecord.IsNull);

        using var document = JsonDocument.Parse(
            storedRecord.ToString());

        var record = document.RootElement;

        Assert.Equal(
            userId,
            record.GetProperty("userId").GetString());

        Assert.Equal(
            admissionId.ToString(),
            record.GetProperty("admissionId").GetString());

        Assert.Equal(
            originalScore.ToString(),
            record.GetProperty("originalScore").GetString());

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            1,
            await database.HashLengthAsync(
                options.AdmissionRecordsKey));
    }

    [Fact]
    public async Task ClaimNextAsync_WhenWaitingQueueEmpty_ShouldReturnQueueEmpty()
    {
        var (store, database, options) = CreateContext();

        var result = await store.ClaimNextAsync(
            AdmissionId.New(),
            maxActivePasses: 10,
            processingLease: TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.QueueEmpty,
            result.Status);

        Assert.Null(result.Claim);

        Assert.Equal(
            0,
            await database.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            0,
            await database.HashLengthAsync(
                options.AdmissionRecordsKey));
    }

    [Fact]
    public async Task ClaimNextAsync_WhenActiveCapacityFull_ShouldNotClaimUser()
    {
        var (store, database, options) = CreateContext();

        var activeExpiry = DateTimeOffset.UtcNow
            .AddMinutes(5)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ActivePassesKey,
            "active-user",
            activeExpiry);

        await database.SortedSetAddAsync(
            options.QueueKey,
            "waiting-user",
            1);

        var result = await store.ClaimNextAsync(
            AdmissionId.New(),
            maxActivePasses: 1,
            processingLease: TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.CapacityFull,
            result.Status);

        Assert.Null(result.Claim);

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.QueueKey));

        Assert.Equal(
            0,
            await database.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            0,
            await database.HashLengthAsync(
                options.AdmissionRecordsKey));
    }

    [Fact]
    public async Task ClaimNextAsync_WhenProcessingCapacityFull_ShouldNotClaimUser()
    {
        var (store, database, options) = CreateContext();

        var leaseExpiry = DateTimeOffset.UtcNow
            .AddMinutes(1)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ProcessingQueueKey,
            "processing-user",
            leaseExpiry);

        await database.SortedSetAddAsync(
            options.QueueKey,
            "waiting-user",
            2);

        var result = await store.ClaimNextAsync(
            AdmissionId.New(),
            maxActivePasses: 1,
            processingLease: TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.CapacityFull,
            result.Status);

        Assert.Null(result.Claim);

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.QueueKey));

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.ProcessingQueueKey));
    }

    [Fact]
    public async Task ClaimNextAsync_ShouldRemoveExpiredActiveBeforeCapacityCheck()
    {
        var (store, database, options) = CreateContext();

        var expiredAt = DateTimeOffset.UtcNow
            .AddMinutes(-1)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ActivePassesKey,
            "expired-active-user",
            expiredAt);

        await database.SortedSetAddAsync(
            options.QueueKey,
            "waiting-user",
            5);

        var result = await store.ClaimNextAsync(
            AdmissionId.New(),
            maxActivePasses: 1,
            processingLease: TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.Claimed,
            result.Status);

        Assert.NotNull(result.Claim);

        Assert.Equal(
            0,
            await database.SortedSetLengthAsync(
                options.ActivePassesKey));

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.ProcessingQueueKey));
    }

    [Fact]
    public async Task ClaimNextAsync_ShouldKeepExpiredProcessingReservedUntilRecovery()
    {
        var (store, database, options) = CreateContext();

        const string expiredUserId =
            "expired-processing-user";

        var expiredLease = DateTimeOffset.UtcNow
            .AddMinutes(-1)
            .ToUnixTimeMilliseconds();

        await database.SortedSetAddAsync(
            options.ProcessingQueueKey,
            expiredUserId,
            expiredLease);

        var admissionRecord = JsonSerializer.Serialize(
            new
            {
                userId = expiredUserId,
                admissionId = AdmissionId.New().ToString(),
                originalScore = "1"
            });

        await database.HashSetAsync(
            options.AdmissionRecordsKey,
            expiredUserId,
            admissionRecord);

        await database.SortedSetAddAsync(
            options.QueueKey,
            "waiting-user",
            2);

        var result = await store.ClaimNextAsync(
            AdmissionId.New(),
            maxActivePasses: 1,
            processingLease: TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.CapacityFull,
            result.Status);

        Assert.Null(result.Claim);

        Assert.NotNull(
            await database.SortedSetScoreAsync(
                options.ProcessingQueueKey,
                expiredUserId));

        Assert.False(
            (await database.HashGetAsync(
                options.AdmissionRecordsKey,
                expiredUserId)).IsNull);

        Assert.Equal(
            1,
            await database.SortedSetLengthAsync(
                options.QueueKey));
    }

    [Fact]
    public async Task ClaimNextAsync_WithConcurrentWorkers_ShouldNeverExceedCapacity()
    {
        var (store, database, options) = CreateContext();

        const int usersCount = 32;
        const int maxCapacity = 4;

        for (var index = 1; index <= usersCount; index++)
        {
            await database.SortedSetAddAsync(
                options.QueueKey,
                $"user-{index}",
                index);
        }

        var tasks = Enumerable.Range(0, usersCount)
            .Select(_ => store.ClaimNextAsync(
                AdmissionId.New(),
                maxActivePasses: maxCapacity,
                processingLease: TimeSpan.FromSeconds(30)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        var claimed = results
            .Where(result =>
                result.Status == AdmissionClaimStatus.Claimed)
            .Select(result =>
                Assert.IsType<AdmissionClaim>(result.Claim))
            .ToArray();

        Assert.Equal(maxCapacity, claimed.Length);

        Assert.Equal(
            usersCount - maxCapacity,
            results.Count(result =>
                result.Status == AdmissionClaimStatus.CapacityFull));

        Assert.Equal(
            maxCapacity,
            await database.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            maxCapacity,
            await database.HashLengthAsync(
                options.AdmissionRecordsKey));

        Assert.Equal(
            usersCount - maxCapacity,
            await database.SortedSetLengthAsync(
                options.QueueKey));

        // No duplicate users.
        Assert.Equal(
            maxCapacity,
            claimed
                .Select(claim => claim.UserId)
                .Distinct()
                .Count());

        Assert.Equal(
            maxCapacity,
            claimed
                .Select(claim => claim.AdmissionId)
                .Distinct()
                .Count());

        Assert.Equal(
            new long[] { 1, 2, 3, 4 },
            claimed
                .Select(claim => claim.OriginalScore)
                .OrderBy(score => score)
                .ToArray());

        var waitingUsers =
            await database.SortedSetRangeByRankAsync(
                options.QueueKey);

        var processingUsers =
            await database.SortedSetRangeByRankAsync(
                options.ProcessingQueueKey);

        var allUsers = waitingUsers
            .Concat(processingUsers)
            .Select(value => value.ToString())
            .ToArray();

        Assert.Equal(usersCount, allUsers.Length);

        Assert.Equal(
            usersCount,
            allUsers.Distinct().Count());
    }

    private (
        RedisAdmissionClaimStore Store,
        IDatabase Database,
        QueueOptions Options) CreateContext()
    {

        var prefix =
            $"gatekeeper-test:{{admission-{Guid.NewGuid():N}}}";

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

        var store = new RedisAdmissionClaimStore(
            _fixture.Connection,
            Options.Create(queueOptions),
            NullLogger<RedisAdmissionClaimStore>.Instance, new Phase03Context(_fixture).Tokens);

        return (
            store,
            _fixture.Connection.GetDatabase(),
            queueOptions);
    }
}
