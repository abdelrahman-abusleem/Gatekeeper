using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Diagnostics;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class WorkerClaimRejectionTests
{
    [Theory]
    [InlineData("ID_EXPIRED")]
    [InlineData("ID_TOO_FUTURE")]
    [InlineData("ID_UNSUPPORTED")]
    [InlineData("ID_INVALID")]
    public async Task IdentityRejection_StopsEachLaneAndRespectsBackoff(string status)
    {
        var store = new FakeStore((id, _) => Reject(id, status));
        var clock = new ManualWorkerClock();
        var options = new QueueOptions { BatchSize = 8, MaxConcurrentAdmissions = 2 };
        using var worker = CreateWorker(store, options, clock);

        await worker.ProcessBatchAsync(respectBackoff: true, recover: false);
        Assert.Equal(2, store.Claims);

        await worker.ProcessBatchAsync(respectBackoff: true, recover: false);
        Assert.Equal(2, store.Claims);

        clock.Advance(options.ReleaseIntervalSeconds);
        await worker.ProcessBatchAsync(respectBackoff: true, recover: false);
        Assert.Equal(4, store.Claims);
        Assert.Equal(0, store.Commits);
    }

    [Theory]
    [InlineData("CLAIM_EXPIRED")]
    [InlineData("ATTEMPT_RESOLVED")]
    public async Task UnavailableReservation_ContinuesWithinTheSameBatch(string status)
    {
        var store = new FakeStore((id, count) => count == 1
            ? Reject(id, status)
            : new AdmissionClaimResult(AdmissionClaimStatus.Claimed,
                new AdmissionClaim(id, "user", 1, DateTime.UtcNow.AddSeconds(30))));
        using var worker = CreateWorker(store, new QueueOptions { BatchSize = 2 }, new ManualWorkerClock());

        await worker.ProcessBatchAsync(recover: false);

        Assert.Equal(2, store.Claims);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task CustomStoreBaseException_StillContinuesWithinTheBatch()
    {
        var store = new FakeStore((id, count) => count == 1
            ? throw new AdmissionClaimUnavailableException("Reservation no longer available.")
            : Claimed(id));
        using var worker = CreateWorker(store, new QueueOptions { BatchSize = 2 }, new ManualWorkerClock());

        await worker.ProcessBatchAsync(recover: false);

        Assert.Equal(2, store.Claims);
        Assert.Equal(1, store.Commits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IdentityRejection_RecordsExactlyOneFailurePerLane_AndKeepsIdentifiersOutOfWarnings(int lanes)
    {
        using var capture = new WorkerDiagnosticsCapture();
        var rejected = new List<AdmissionId>();
        var store = new FakeStore((id, _) =>
        {
            rejected.Add(id);
            return Reject(id, "ID_TOO_FUTURE", capture);
        });
        using var worker = CreateWorker(store, new QueueOptions { BatchSize = 8, MaxConcurrentAdmissions = lanes },
            new ManualWorkerClock(), capture);

        await worker.ProcessBatchAsync(respectBackoff: true, recover: false);

        Assert.Equal(lanes, store.Claims);
        Assert.Equal(lanes, capture.Failures.Count);
        Assert.All(capture.Failures, failure => Assert.Equal((AdmissionMetricCategories.ProtocolOrDependency, 1L), failure));
        Assert.Equal(lanes, capture.Logs.Count(log => log.Level == LogLevel.Warning));
        Assert.All(rejected, id =>
        {
            Assert.Contains(capture.Logs, log => log.Level == LogLevel.Debug && log.Message.Contains(id.ToString()));
            Assert.All(capture.Logs.Where(log => log.Level >= LogLevel.Warning),
                log => Assert.DoesNotContain(id.ToString(), log.Message));
        });
    }

    [Fact]
    public async Task IdentityRejection_DoesNotStopOtherLanesFromClaiming()
    {
        var store = new FakeStore((id, count) => count == 1 ? Reject(id, "ID_TOO_FUTURE") : Claimed(id));
        var options = new QueueOptions { BatchSize = 2, MaxConcurrentAdmissions = 2 };
        using var worker = CreateWorker(store, options, new ManualWorkerClock());

        await worker.ProcessBatchAsync(recover: false);

        Assert.Equal(2, store.Claims);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task ConsecutiveIdentityRejections_GrowBackoffOncePerFailure_AndCommitResetsIt()
    {
        var store = new FakeStore((id, count) => count is 1 or 2 or 4 ? Reject(id, "ID_TOO_FUTURE") : Claimed(id));
        var clock = new ManualWorkerClock();
        var options = new QueueOptions { BatchSize = 1, ReleaseIntervalSeconds = 2, WorkerRetryMaxDelaySeconds = 10 };
        using var worker = CreateWorker(store, options, clock);

        Task Pass() => worker.ProcessBatchAsync(respectBackoff: true, recover: false);
        await Pass();
        Assert.Equal(1, store.Claims);
        clock.Advance(2);
        await Pass();
        Assert.Equal(2, store.Claims);
        clock.Advance(2);
        await Pass();
        Assert.Equal(2, store.Claims);
        clock.Advance(2);
        await Pass();
        Assert.Equal(1, store.Commits);

        await Pass();
        Assert.Equal(4, store.Claims);
        clock.Advance(2);
        await Pass();
        Assert.Equal(5, store.Claims);
        Assert.Equal(2, store.Commits);
    }

    private static AdmissionClaimResult Claimed(AdmissionId id) =>
        new(AdmissionClaimStatus.Claimed, new AdmissionClaim(id, "user", 1, DateTime.UtcNow.AddSeconds(30)));

    private static AdmissionClaimResult Reject(AdmissionId id, string status, ILogger<RedisAdmissionClaimStore>? logger = null) =>
        RedisAdmissionResponses.ParseClaimResult(RedisResult.Create(new RedisValue[] { status }), id,
            logger ?? NullLogger<RedisAdmissionClaimStore>.Instance);

    private static QueueProcessorWorker CreateWorker(FakeStore store, QueueOptions options, TimeProvider clock, ILogger<QueueProcessorWorker>? logger = null) =>
        new(store, new TestTokens(), new CallbackNotifier(_ => Task.CompletedTask), Options.Create(options),
            logger ?? NullLogger<QueueProcessorWorker>.Instance, clock);

    private sealed class TestTokens : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => "test-pass";
        public Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeStore(Func<AdmissionId, int, AdmissionClaimResult> claim) : IAdmissionClaimStore
    {
        public int Claims { get; private set; }
        public int Commits { get; private set; }

        public Task<AdmissionClaimResult> ClaimNextAsync(AdmissionId id, int max, TimeSpan lease, CancellationToken ct = default) =>
            Task.FromResult(claim(id, ++Claims));

        public Task CommitAsync(AdmissionClaim value, string token, DateTime expires, CancellationToken ct = default)
        {
            Commits++;
            return Task.CompletedTask;
        }

        public Task<AdmissionCommitStatus> GetCommitStatusAsync(AdmissionClaim value, string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionRecoveryStatus> RecoverAsync(AdmissionClaim value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(int batch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionStatus> GetStatusAsync(string user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetPassAsync(string user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionValidationStatus> ValidateAdmissionAsync(string token, string user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionReleaseStatus> ReleaseAsync(string user, AdmissionId id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
