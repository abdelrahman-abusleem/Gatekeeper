using Gatekeeper.Domain.Exceptions;

using System.Text.Json;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RedisAdmissionCommitTests
    : IClassFixture<RedisClaimFixture>
{
    private readonly RedisClaimFixture _fixture;

    public RedisAdmissionCommitTests(
        RedisClaimFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CommitAsync_ShouldPromoteProcessingToActiveAtomically()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 42);

        const string token = "signed-jwt-test-value";

        var expiresAtUtc = DateTime.UtcNow.AddMinutes(5);

        await store.CommitAsync(
            claim,
            token,
            expiresAtUtc);

        var expectedExpiryMs =
            new DateTimeOffset(expiresAtUtc)
                .ToUnixTimeMilliseconds();

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.QueueKey,
                claim.UserId));

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.ProcessingQueueKey,
                claim.UserId));

        Assert.True(
            (await db.HashGetAsync(
                options.AdmissionRecordsKey,
                claim.UserId)).IsNull);

        Assert.Equal(
            (double)expectedExpiryMs,
            await db.SortedSetScoreAsync(
                options.ActivePassesKey,
                claim.UserId));

        Assert.Equal(
            token,
            (string?)await db.StringGetAsync(
                TokenKey(options, claim.UserId)));

        Assert.Equal(
            claim.AdmissionId.ToString(),
            (string?)await db.StringGetAsync(
                ActiveIdentityKey(options, claim.UserId)));

        var tokenTtl = await db.KeyTimeToLiveAsync(
            TokenKey(options, claim.UserId));

        var identityTtl = await db.KeyTimeToLiveAsync(
            ActiveIdentityKey(options, claim.UserId));

        Assert.NotNull(tokenTtl);
        Assert.NotNull(identityTtl);

        Assert.True(tokenTtl.Value > TimeSpan.Zero);
        Assert.True(identityTtl.Value > TimeSpan.Zero);

        // PXAT uses Redis's clock, which need not match the test host's clock.
        Assert.Equal(expectedExpiryMs, (long)await db.ExecuteAsync("PEXPIRETIME", TokenKey(options, claim.UserId)));
        Assert.Equal(expectedExpiryMs, (long)await db.ExecuteAsync("PEXPIRETIME", ActiveIdentityKey(options, claim.UserId)));

        var storedAttempt = await db.StringGetAsync(
            AttemptKey(options, claim.AdmissionId));

        Assert.False(storedAttempt.IsNull);

        using var document = JsonDocument.Parse(
            storedAttempt.ToString());

        var attempt = document.RootElement;

        Assert.Equal(
            "COMMITTED",
            attempt.GetProperty("status").GetString());

        Assert.Equal(
            claim.AdmissionId.ToString(),
            attempt.GetProperty("admissionId").GetString());

        Assert.Equal(
            expectedExpiryMs,
            attempt.GetProperty("passExpiresAt").GetInt64());
    }

    [Fact]
    public async Task CommitAsync_WithWrongAdmissionId_ShouldRejectWithoutChanges()
    {
        var (store, db, options) = CreateContext();

        var originalClaim = await ClaimUserAsync(
            store, db, options, "user-1", 10);

        var wrongClaim = originalClaim with
        {
            AdmissionId = AdmissionId.New()
        };

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                wrongClaim,
                "wrong-token",
                DateTime.UtcNow.AddMinutes(5)));

        await AssertStillProcessingAsync(
            db, options, originalClaim);

        Assert.False(
            await db.KeyExistsAsync(
                TokenKey(options, originalClaim.UserId)));

        Assert.False(
            await db.KeyExistsAsync(
                ActiveIdentityKey(options, originalClaim.UserId)));
    }

    [Fact]
    public async Task CommitAsync_WithExpiredLease_ShouldRejectAndKeepProcessing()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store,
            db,
            options,
            "user-1",
            5,
            TimeSpan.FromMilliseconds(150));

        await Task.Delay(400);

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "late-token",
                DateTime.UtcNow.AddMinutes(5)));

        await AssertStillProcessingAsync(
            db, options, claim);

        Assert.False(
            await db.KeyExistsAsync(
                TokenKey(options, claim.UserId)));
    }

    [Fact]
    public async Task CommitAsync_WithoutAdmissionRecord_ShouldReject()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 8);

        await db.HashDeleteAsync(
            options.AdmissionRecordsKey,
            claim.UserId);

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "test-token",
                DateTime.UtcNow.AddMinutes(5)));

        Assert.NotNull(
            await db.SortedSetScoreAsync(
                options.ProcessingQueueKey,
                claim.UserId));

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.ActivePassesKey,
                claim.UserId));

        Assert.False(
            await db.KeyExistsAsync(
                TokenKey(options, claim.UserId)));
    }

    [Fact]
    public async Task CommitAsync_WithoutClaimAttempt_ShouldReject()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 9);

        await db.KeyDeleteAsync(
            AttemptKey(options, claim.AdmissionId));

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "test-token",
                DateTime.UtcNow.AddMinutes(5)));

        await AssertStillProcessingAsync(
            db, options, claim);
    }

    [Fact]
    public async Task CommitAsync_WhenLeaseDoesNotMatchAttempt_ShouldReject()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 11);

        var differentLeaseMs =
            new DateTimeOffset(claim.LeaseExpiresAtUtc)
                .ToUnixTimeMilliseconds() + 5000;

        await db.SortedSetAddAsync(
            options.ProcessingQueueKey,
            claim.UserId,
            differentLeaseMs);

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "test-token",
                DateTime.UtcNow.AddMinutes(5)));

        Assert.Equal(
            (double)differentLeaseMs,
            await db.SortedSetScoreAsync(
                options.ProcessingQueueKey,
                claim.UserId));

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.ActivePassesKey,
                claim.UserId));

        Assert.False(
            await db.KeyExistsAsync(
                TokenKey(options, claim.UserId)));
    }

    [Fact]
    public async Task CommitAsync_WithExpiredPassTime_ShouldReject()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 12);

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "expired-token",
                DateTime.UtcNow.AddMinutes(-1)));

        await AssertStillProcessingAsync(
            db, options, claim);
    }

    [Fact]
    public async Task CommitAsync_WhenUserAlreadyActive_ShouldReject()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 15);

        var activeExpiry = DateTimeOffset.UtcNow
            .AddMinutes(5)
            .ToUnixTimeMilliseconds();

        // Deliberately create an inconsistent duplicate state.
        await db.SortedSetAddAsync(
            options.ActivePassesKey,
            claim.UserId,
            activeExpiry);

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "test-token",
                DateTime.UtcNow.AddMinutes(5)));

        Assert.NotNull(
            await db.SortedSetScoreAsync(
                options.ProcessingQueueKey,
                claim.UserId));

        Assert.Equal(
            (double)activeExpiry,
            await db.SortedSetScoreAsync(
                options.ActivePassesKey,
                claim.UserId));

        Assert.False(
            await db.KeyExistsAsync(
                TokenKey(options, claim.UserId)));
    }

    [Fact]
    public async Task CommitAsync_WhenCalledAgain_ShouldNotOverwriteActivePass()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 20);

        var expiresAtUtc = DateTime.UtcNow.AddMinutes(5);

        const string originalToken = "original-test-token";

        await store.CommitAsync(
            claim,
            originalToken,
            expiresAtUtc);

        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(
            () => store.CommitAsync(
                claim,
                "replacement-token",
                expiresAtUtc.AddMinutes(1)));

        Assert.Equal(
            originalToken,
            (string?)await db.StringGetAsync(
                TokenKey(options, claim.UserId)));

        Assert.Equal(
            claim.AdmissionId.ToString(),
            (string?)await db.StringGetAsync(
                ActiveIdentityKey(options, claim.UserId)));

        Assert.Equal(
            (double)new DateTimeOffset(expiresAtUtc)
                .ToUnixTimeMilliseconds(),
            await db.SortedSetScoreAsync(
                options.ActivePassesKey,
                claim.UserId));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ActivePassesKey));
    }

    [Fact]
    public async Task CommitAsync_WithConcurrentRequests_ShouldCommitOnlyOnce()
    {
        var (store, db, options) = CreateContext();

        var claim = await ClaimUserAsync(
            store, db, options, "user-1", 30);

        var expiresAtUtc = DateTime.UtcNow.AddMinutes(5);

        const int concurrentRequests = 16;

        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(async _ =>
            {
                try
                {
                    await store.CommitAsync(
                        claim,
                        "same-test-token",
                        expiresAtUtc);

                    return true;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(
            1,
            results.Count(succeeded => succeeded));

        Assert.Equal(
            1,
            await db.SortedSetLengthAsync(
                options.ActivePassesKey));

        Assert.Equal(
            0,
            await db.SortedSetLengthAsync(
                options.ProcessingQueueKey));

        Assert.Equal(
            0,
            await db.HashLengthAsync(
                options.AdmissionRecordsKey));

        Assert.Equal(
            "same-test-token",
            (string?)await db.StringGetAsync(
                TokenKey(options, claim.UserId)));

        Assert.Equal(
            claim.AdmissionId.ToString(),
            (string?)await db.StringGetAsync(
                ActiveIdentityKey(options, claim.UserId)));
    }

    private async Task<AdmissionClaim> ClaimUserAsync(
        RedisAdmissionClaimStore store,
        IDatabase db,
        QueueOptions options,
        string userId,
        long originalScore,
        TimeSpan? lease = null)
    {
        await db.SortedSetAddAsync(
            options.QueueKey,
            userId,
            originalScore);

        var result = await store.ClaimNextAsync(
            AdmissionId.New(),
            maxActivePasses: 10,
            processingLease: lease ?? TimeSpan.FromSeconds(30));

        Assert.Equal(
            AdmissionClaimStatus.Claimed,
            result.Status);

        return Assert.IsType<AdmissionClaim>(
            result.Claim);
    }

    private static async Task AssertStillProcessingAsync(
        IDatabase db,
        QueueOptions options,
        AdmissionClaim claim)
    {
        Assert.NotNull(
            await db.SortedSetScoreAsync(
                options.ProcessingQueueKey,
                claim.UserId));

        Assert.False(
            (await db.HashGetAsync(
                options.AdmissionRecordsKey,
                claim.UserId)).IsNull);

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.ActivePassesKey,
                claim.UserId));

        Assert.Null(
            await db.SortedSetScoreAsync(
                options.QueueKey,
                claim.UserId));
    }

    private static RedisKey TokenKey(
        QueueOptions options,
        string userId)
    {
        return options.PassKeyPrefix + userId;
    }

    private static RedisKey ActiveIdentityKey(
        QueueOptions options,
        string userId)
    {
        return options.ActiveAdmissionKeyPrefix + userId;
    }

    private static RedisKey AttemptKey(
        QueueOptions options,
        AdmissionId admissionId)
    {
        return options.ClaimAttemptKeyPrefix +
            admissionId.ToString();
    }

    private (
        RedisAdmissionClaimStore Store,
        IDatabase Database,
        QueueOptions Options) CreateContext()
    {

        var prefix =
            $"gatekeeper-test:{{commit-{Guid.NewGuid():N}}}";

        var queueOptions = new QueueOptions
        {
            QueueKey = $"{prefix}:waiting",
            ProcessingQueueKey = $"{prefix}:processing",
            AdmissionRecordsKey = $"{prefix}:records",
            ActivePassesKey = $"{prefix}:active",
            QueueSequenceKey = $"{prefix}:sequence",
            ClaimAttemptKeyPrefix = $"{prefix}:claim_attempt:",
            PassKeyPrefix = $"{prefix}:pass:",
            ActiveAdmissionKeyPrefix = $"{prefix}:active_id:"
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
