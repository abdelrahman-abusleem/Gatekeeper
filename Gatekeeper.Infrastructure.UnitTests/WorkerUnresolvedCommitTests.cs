using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class WorkerUnresolvedCommitTests
{
    private const string Parked = "unresolved_parked";

    [Fact]
    public async Task ProlongedInconsistency_ParksOnceAndClaimsFreshIdentity()
    {
        using var test = new Scenario();
        test.Store.Users.Enqueue("next-user");
        await test.StartUnresolvedAsync();
        test.Clock.Advance(361);
        await test.StepAsync();

        Assert.Equal(2, test.Store.Claims.Count);
        Assert.NotEqual(test.Store.Claims[0], test.Store.Claims[1]);
        Assert.Equal(new[] { "stuck-user", "next-user" }, test.Store.Commits.Select(x => x.Claim.UserId));
        Assert.All(test.Store.Reconciliations, x => Assert.Equal(test.Store.Commits[0], x));
        Assert.Equal(new[] { "next-user" }, test.Notified);
        await test.StepAsync();
        AssertParkedOnce(test);
    }

    [Theory]
    [InlineData(AdmissionCommitStatus.Unknown)]
    [InlineData(AdmissionCommitStatus.Mismatch)]
    [InlineData(AdmissionCommitStatus.Inconsistent)]
    public async Task BeforeThreshold_RetainsPendingClaimAndToken(AdmissionCommitStatus status)
    {
        using var test = new Scenario();
        test.Store.Status = () => status;
        await test.StartUnresolvedAsync();
        test.Clock.Advance(359);
        await test.StepAsync();

        Assert.Single(test.Store.Claims);
        var commit = Assert.Single(test.Store.Commits);
        Assert.Equal(new[] { commit, commit }, test.Store.Reconciliations);
        AssertNotParked(test);
    }

    [Fact]
    public async Task ReconciliationExceptions_DoNotAdvanceUnresolvedTime()
    {
        using var test = new Scenario();
        await test.StartUnresolvedAsync();
        test.Clock.Advance(180);
        await test.StepAsync();
        test.Store.Status = () => throw new TimeoutException();
        test.Clock.Advance(1000);
        await Assert.ThrowsAsync<TimeoutException>(test.StepAsync);
        AssertNotParked(test);

        test.Store.Status = () => AdmissionCommitStatus.Mismatch;
        test.Clock.Advance(1000);
        await test.StepAsync();
        test.Clock.Advance(179);
        await test.StepAsync();
        Assert.Single(test.Store.Claims);
        AssertNotParked(test);

        test.Clock.Advance(1);
        await test.StepAsync();
        AssertParkedOnce(test);
    }

    [Fact]
    public async Task DefinitiveNotCommitted_ResetsUnresolvedTimeBeforeRetry()
    {
        using var test = new Scenario();
        await test.StartUnresolvedAsync();
        test.Clock.Advance(359);
        await test.StepAsync();
        test.Store.Status = () => AdmissionCommitStatus.NotCommitted;
        test.Store.CommitFailuresRemaining = 1;
        await Assert.ThrowsAsync<TimeoutException>(test.StepAsync);
        Assert.Equal(new[] { test.Store.Commits[0], test.Store.Commits[0] }, test.Store.Commits);

        test.Store.Status = () => AdmissionCommitStatus.Unknown;
        test.Clock.Advance(360);
        await test.StepAsync();
        Assert.Single(test.Store.Claims);
        AssertNotParked(test);

        test.Clock.Advance(360);
        await test.StepAsync();
        AssertParkedOnce(test);
    }

    [Fact]
    public async Task ParkingOneLane_DoesNotBlockHealthyAdmissions()
    {
        using var test = new Scenario(lanes: 2);
        test.Store.Users.Enqueue("healthy-first");
        test.Store.Users.Enqueue(null);
        await Assert.ThrowsAsync<TimeoutException>(test.StepAsync);

        test.Store.Users.Enqueue("healthy-before-park");
        test.Store.Users.Enqueue(null);
        await test.StepAsync();
        Assert.Equal(new[] { "healthy-first", "healthy-before-park" }, test.Notified);
        AssertNotParked(test);

        test.Clock.Advance(360);
        test.Store.Users.Enqueue("healthy-after-park");
        await test.StepAsync();
        Assert.Equal(new[] { "healthy-first", "healthy-before-park", "healthy-after-park" }, test.Notified);
        Assert.Single(test.Store.Commits, x => x.Claim.UserId == "stuck-user");
        AssertParkedOnce(test);
    }

    private static void AssertNotParked(Scenario test)
    {
        Assert.DoesNotContain(test.Diagnostics.Failures, failure => failure.Category == Parked);
        Assert.DoesNotContain(test.Diagnostics.Logs, log => log.Level >= LogLevel.Error);
    }

    private static void AssertParkedOnce(Scenario test)
    {
        Assert.Equal((Parked, 1L), Assert.Single(test.Diagnostics.Failures, failure => failure.Category == Parked));
        var error = Assert.Single(test.Diagnostics.Logs, log => log.Level >= LogLevel.Error);
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.DoesNotContain("stuck-user", error.Message);
        Assert.DoesNotContain(test.Store.Claims[0].ToString(), error.Message);
        Assert.DoesNotContain("test-pass", error.Message);
    }

    private sealed class Scenario : IDisposable
    {
        internal ManualWorkerClock Clock { get; } = new();
        internal WorkerDiagnosticsCapture Diagnostics { get; } = new();
        internal FakeStore Store { get; } = new();
        internal List<string> Notified { get; } = [];
        private readonly QueueProcessorWorker _worker;

        internal Scenario(int lanes = 1)
        {
            Store.Users.Enqueue("stuck-user");
            _worker = new(Store, new TestTokens(), new CallbackNotifier(user =>
            {
                Notified.Add(user);
                return Task.CompletedTask;
            }), Options.Create(new QueueOptions { MaxConcurrentAdmissions = lanes, BatchSize = lanes == 1 ? 2 : 3 }), Diagnostics, Clock);
        }

        internal async Task StartUnresolvedAsync()
        {
            await Assert.ThrowsAsync<TimeoutException>(StepAsync);
            await StepAsync();
            Assert.Single(Store.Claims);
        }

        internal Task StepAsync() => _worker.ProcessBatchAsync(recover: false);

        public void Dispose()
        {
            _worker.Dispose();
            Diagnostics.Dispose();
        }
    }

    private sealed class TestTokens : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => "test-pass";
        public Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeStore : IAdmissionClaimStore
    {
        internal Queue<string?> Users { get; } = new();
        internal List<AdmissionId> Claims { get; } = [];
        internal List<(AdmissionClaim Claim, string Token)> Commits { get; } = [];
        internal List<(AdmissionClaim Claim, string Token)> Reconciliations { get; } = [];
        internal Func<AdmissionCommitStatus> Status { get; set; } = () => AdmissionCommitStatus.Inconsistent;
        internal int CommitFailuresRemaining { get; set; } = 1;

        public Task<AdmissionClaimResult> ClaimNextAsync(AdmissionId id, int max, TimeSpan lease, CancellationToken ct = default)
        {
            Claims.Add(id);
            var result = Users.TryDequeue(out var user) && user is not null
                ? new AdmissionClaimResult(AdmissionClaimStatus.Claimed,
                    new AdmissionClaim(id, user, Claims.Count, DateTime.UnixEpoch.AddSeconds(30)))
                : new AdmissionClaimResult(AdmissionClaimStatus.QueueEmpty, null);
            return Task.FromResult(result);
        }

        public Task CommitAsync(AdmissionClaim value, string token, DateTime expires, CancellationToken ct = default)
        {
            Commits.Add((value, token));
            if (CommitFailuresRemaining > 0)
            {
                CommitFailuresRemaining--;
                throw new TimeoutException();
            }
            return Task.CompletedTask;
        }

        public Task<AdmissionCommitStatus> GetCommitStatusAsync(AdmissionClaim value, string token, CancellationToken ct = default)
        {
            Reconciliations.Add((value, token));
            return Task.FromResult(Status());
        }

        public Task<AdmissionRecoveryStatus> RecoverAsync(AdmissionClaim value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(int batch, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionStatus> GetStatusAsync(string user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetPassAsync(string user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionValidationStatus> ValidateAdmissionAsync(string token, string user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AdmissionReleaseStatus> ReleaseAsync(string user, AdmissionId id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
