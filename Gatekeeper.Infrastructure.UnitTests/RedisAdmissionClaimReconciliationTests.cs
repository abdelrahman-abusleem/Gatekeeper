
using System.Text.Json;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RedisAdmissionClaimReconciliationTests
    : IClassFixture<RedisClaimFixture>
{
    private readonly RedisClaimFixture _fixture;

    public RedisAdmissionClaimReconciliationTests(
        RedisClaimFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RetryingSameAdmissionId_ShouldReturnSameClaim()
    {
        var (store, db, options) = CreateContext();

        await db.SortedSetAddAsync(
            options.QueueKey, "user-1", 10);

        await db.SortedSetAddAsync(
            options.QueueKey, "user-2", 20);

        var admissionId = AdmissionId.New();

        var first = await store.ClaimNextAsync(
            admissionId,
            2,
            TimeSpan.FromSeconds(30));

        var second = await store.ClaimNextAsync(
            admissionId,
            2,
            TimeSpan.FromSeconds(30));

        Assert.Equal(AdmissionClaimStatus.Claimed, first.Status);
        Assert.Equal(AdmissionClaimStatus.Claimed, second.Status);

        var firstClaim = Assert.IsType<AdmissionClaim>(
            first.Claim);

        var secondClaim = Assert.IsType<AdmissionClaim>(
            second.Claim);

        Assert.Equal(firstClaim, secondClaim);

        Assert.Equal("user-1", secondClaim.UserId);
        Assert.Equal(10L, secondClaim.OriginalScore);

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.QueueKey));

        Assert.NotNull(
            await db.SortedSetScoreAsync(
                options.QueueKey, "user-2"));

        Assert.Equal(
            1,
            await db.HashLengthAsync(
                options.AdmissionRecordsKey));

        var attemptKey =
            options.ClaimAttemptKeyPrefix +
            admissionId.ToString();

        Assert.True(await db.KeyExistsAsync(attemptKey));
    }

    [Fact]
    public async Task RetryingSameAdmissionId_WhenCapacityFull_ShouldNotReserveAnotherSlot()
    {
        var (store, db, options) = CreateContext();

        await db.SortedSetAddAsync(
            options.QueueKey, "user-1", 1);

        await db.SortedSetAddAsync(
            options.QueueKey, "user-2", 2);

        var admissionId = AdmissionId.New();

        var first = await store.ClaimNextAsync(
            admissionId,
            1,
            TimeSpan.FromSeconds(30));

        Assert.Equal(AdmissionClaimStatus.Claimed, first.Status);


        var retry = await store.ClaimNextAsync(
            admissionId,
            1,
            TimeSpan.FromSeconds(30));

        Assert.Equal(AdmissionClaimStatus.Claimed, retry.Status);

        Assert.Equal(first.Claim, retry.Claim);

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.QueueKey));
    }

    [Fact]
    public async Task RetryingExpiredClaim_ShouldRejectAndKeepStateForRecovery()
    {
        var (store, db, options) = CreateContext();

        const string userId = "user-1";

        await db.SortedSetAddAsync(
            options.QueueKey, userId, 15);

        var admissionId = AdmissionId.New();

        var first = await store.ClaimNextAsync(
            admissionId,
            1,
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(AdmissionClaimStatus.Claimed, first.Status);

        await Task.Delay(350);

        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(
            async () =>
            {
                await store.ClaimNextAsync(
                    admissionId,
                    1,
                    TimeSpan.FromMilliseconds(100));
            });

        Assert.NotNull(
            await db.SortedSetScoreAsync(
                options.ProcessingQueueKey, userId));

        Assert.False(
            (await db.HashGetAsync(
                options.AdmissionRecordsKey, userId)).IsNull);

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.QueueKey, userId));
    }

    [Fact]
    public async Task RetryingClaim_WhenAdmissionRecordMissing_ShouldNotClaimAnotherUser()
    {
        var (store, db, options) = CreateContext();

        await db.SortedSetAddAsync(
            options.QueueKey, "user-1", 1);

        await db.SortedSetAddAsync(
            options.QueueKey, "user-2", 2);

        var admissionId = AdmissionId.New();

        var first = await store.ClaimNextAsync(
            admissionId,
            2,
            TimeSpan.FromSeconds(30));

        Assert.Equal(AdmissionClaimStatus.Claimed, first.Status);

        await db.HashDeleteAsync(
            options.AdmissionRecordsKey, "user-1");

        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(
            async () =>
            {
                await store.ClaimNextAsync(
                    admissionId,
                    2,
                    TimeSpan.FromSeconds(30));
            });

        Assert.NotNull(
            await db.SortedSetScoreAsync(
                options.QueueKey, "user-2"));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.QueueKey));
    }

    [Fact]
    public async Task RetryingClaim_WhenAdmissionRecordReplaced_ShouldRejectOldAttempt()
    {
        var (store, db, options) = CreateContext();

        const string userId = "user-1";

        await db.SortedSetAddAsync(
            options.QueueKey, userId, 42);

        var originalAdmissionId = AdmissionId.New();

        var first = await store.ClaimNextAsync(
            originalAdmissionId,
            2,
            TimeSpan.FromSeconds(30));

        Assert.Equal(AdmissionClaimStatus.Claimed, first.Status);

        var replacementId = AdmissionId.New();

        var replacementRecord = JsonSerializer.Serialize(
            new
            {
                userId,
                admissionId = replacementId.ToString(),
                originalScore = "42"
            });


        await db.HashSetAsync(
            options.AdmissionRecordsKey,
            userId,
            replacementRecord);

        await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(
            async () =>
            {
                await store.ClaimNextAsync(
                    originalAdmissionId,
                    2,
                    TimeSpan.FromSeconds(30));
            });

        var storedRecord = await db.HashGetAsync(
            options.AdmissionRecordsKey, userId);

        using var document = JsonDocument.Parse(
            storedRecord.ToString());

        Assert.Equal(
            replacementId.ToString(),
            document.RootElement
                .GetProperty("admissionId")
                .GetString());

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ProcessingQueueKey));
    }

    [Fact]
    public async Task DifferentAdmissionId_WhenCapacityFull_ShouldNotClaimAnotherUser()
    {
        var (store, db, options) = CreateContext();

        await db.SortedSetAddAsync(
            options.QueueKey, "user-1", 1);

        await db.SortedSetAddAsync(
            options.QueueKey, "user-2", 2);

        var firstAdmissionId = AdmissionId.New();
        var secondAdmissionId = AdmissionId.New();

        var first = await store.ClaimNextAsync(
            firstAdmissionId,
            1,
            TimeSpan.FromSeconds(30));

        var second = await store.ClaimNextAsync(
            secondAdmissionId,
            1,
            TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.Claimed,
            first.Status);

        Assert.Equal(
            AdmissionClaimStatus.CapacityFull,
            second.Status);

        Assert.Null(second.Claim);

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.QueueKey));

        var secondAttemptKey =
            options.ClaimAttemptKeyPrefix +
            secondAdmissionId.ToString();

        Assert.True(
            await db.KeyExistsAsync(secondAttemptKey));

        using var outcome = JsonDocument.Parse((await db.StringGetAsync(secondAttemptKey)).ToString());
        Assert.Equal("CAPACITY_FULL", outcome.RootElement.GetProperty("status").GetString());
    }

    private (
        RedisAdmissionClaimStore Store,
        IDatabase Database,
        QueueOptions Options) CreateContext()
    {

        var prefix =
            $"gatekeeper-test:{{reconcile-{Guid.NewGuid():N}}}";

        var queueOptions = new QueueOptions
        {
            PassKeyPrefix = $"{prefix}:pass:",
            ActiveAdmissionKeyPrefix = $"{prefix}:active_id:",
            QueueKey = $"{prefix}:waiting",
            ProcessingQueueKey = $"{prefix}:processing",
            AdmissionRecordsKey = $"{prefix}:records",
            ActivePassesKey = $"{prefix}:active",
            QueueSequenceKey = $"{prefix}:sequence",
            ClaimAttemptKeyPrefix = $"{prefix}:claim_attempt:"
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
