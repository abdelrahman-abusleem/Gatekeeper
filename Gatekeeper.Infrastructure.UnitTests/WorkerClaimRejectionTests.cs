using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
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

    private static AdmissionClaimResult Reject(AdmissionId id, string status) =>
        RedisAdmissionResponses.ParseClaimResult(RedisResult.Create(new RedisValue[] { status }), id,
            NullLogger<RedisAdmissionClaimStore>.Instance);

    private static QueueProcessorWorker CreateWorker(FakeStore store, QueueOptions options, TimeProvider clock) =>
        new(store, new TestTokens(), new CallbackNotifier(_ => Task.CompletedTask), Options.Create(options),
            NullLogger<QueueProcessorWorker>.Instance, clock);

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
