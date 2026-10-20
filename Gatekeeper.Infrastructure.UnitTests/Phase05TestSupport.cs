using System.Collections.Concurrent;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Infrastructure.UnitTests;

internal sealed class ManualWorkerClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
}

internal sealed class ObservedClaimStore(IAdmissionClaimStore inner) : IAdmissionClaimStore
{
    public Task<AdmissionValidationStatus> ValidateAdmissionAsync(string token, string user, CancellationToken ct = default)
        => inner.ValidateAdmissionAsync(token, user, ct);
    public Task<AdmissionReleaseStatus> ReleaseAsync(string user, AdmissionId id, CancellationToken ct = default)
        => inner.ReleaseAsync(user, id, ct);
    public ConcurrentQueue<AdmissionId> Ids { get; } = new();
    public ConcurrentQueue<(AdmissionClaim Claim, string Token, DateTime Expiry)> Commits { get; } = new();
    public Func<AdmissionId, CancellationToken, Task>? BeforeClaim { get; set; }
    public Func<AdmissionClaimResult, CancellationToken, Task>? AfterClaim { get; set; }
    public Func<AdmissionClaim, CancellationToken, Task>? BeforeCommit { get; set; }
    public Func<AdmissionClaim, CancellationToken, Task>? AfterCommit { get; set; }
    public Func<AdmissionClaim, CancellationToken, Task<AdmissionCommitStatus?>>? Status { get; set; }
    public Action<IReadOnlyList<AdmissionRecoveryResult>>? AfterRecovery { get; set; }
    public int RecoveryCalls;
    public int StatusCalls;
    public int RecoveredUsers;

    public async Task<AdmissionClaimResult> ClaimNextAsync(AdmissionId id, int max, TimeSpan lease, CancellationToken ct = default)
    {
        Ids.Enqueue(id);
        if (BeforeClaim is not null) await BeforeClaim(id, ct);
        var result = await inner.ClaimNextAsync(id, max, lease, ct);
        if (AfterClaim is not null) await AfterClaim(result, ct);
        return result;
    }
    public async Task CommitAsync(AdmissionClaim claim, string token, DateTime expiry, CancellationToken ct = default)
    {
        Commits.Enqueue((claim, token, expiry));
        if (BeforeCommit is not null) await BeforeCommit(claim, ct);
        await inner.CommitAsync(claim, token, expiry, ct);
        if (AfterCommit is not null) await AfterCommit(claim, ct);
    }
    public async Task<AdmissionCommitStatus> GetCommitStatusAsync(AdmissionClaim claim, string token, CancellationToken ct = default)
    {
        Interlocked.Increment(ref StatusCalls);
        return (Status is null ? null : await Status(claim, ct)) ?? await inner.GetCommitStatusAsync(claim, token, ct);
    }
    public Task<AdmissionRecoveryStatus> RecoverAsync(AdmissionClaim claim, CancellationToken ct = default) => inner.RecoverAsync(claim, ct);
    public async Task<IReadOnlyList<AdmissionRecoveryResult>> RecoverExpiredAsync(int batch, CancellationToken ct = default)
    {
        Interlocked.Increment(ref RecoveryCalls);
        var result = await inner.RecoverExpiredAsync(batch, ct);
        Interlocked.Add(ref RecoveredUsers, result.Count(x => x.Status == AdmissionRecoveryStatus.Recovered));
        AfterRecovery?.Invoke(result);
        return result;
    }
    public Task<AdmissionStatus> GetStatusAsync(string user, CancellationToken ct = default) => inner.GetStatusAsync(user, ct);
    public Task<string?> GetPassAsync(string user, CancellationToken ct = default) => inner.GetPassAsync(user, ct);
}
