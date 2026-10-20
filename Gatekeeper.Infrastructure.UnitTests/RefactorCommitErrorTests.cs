using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Infrastructure.Services;
using StackExchange.Redis;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorCommitErrorTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailure_BackoffAndReconciliationPreservePreparedAttempt(bool definitiveRejection)
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var observed = new ObservedClaimStore(c.Store)
        {
            BeforeCommit = (_, _) => throw (definitiveRejection
                ? new AdmissionCommitRejectedException("Explicit rejection.")
                : new InvalidOperationException("Protocol failure."))
        };
        var clock = new ManualWorkerClock();
        using var worker = new QueueProcessorWorker(observed, c.Tokens, new CallbackNotifier(_ => Task.CompletedTask),
            Options.Create(c.Options), NullLogger<QueueProcessorWorker>.Instance, clock);
        if (definitiveRejection) await worker.ProcessBatchAsync(respectBackoff: true);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessBatchAsync(respectBackoff: true));
        var prepared = Assert.Single(observed.Commits);
        Assert.Equal((0L, 1L, 0L), await c.CountsAsync());
        observed.BeforeCommit = null;
        clock.Advance(0.999);
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Single(observed.Commits);
        Assert.Equal(0, observed.StatusCalls);
        clock.Advance(0.001);
        await worker.ProcessBatchAsync(respectBackoff: true);
        Assert.Single(observed.Ids);
        Assert.Equal(1, observed.StatusCalls);
        Assert.Equal(2, observed.Commits.Count);
        Assert.All(observed.Commits, commit => Assert.Equal(prepared, commit));
        Assert.Equal(prepared.Token, await c.Store.GetPassAsync("user"));
    }

    [Theory]
    [InlineData("INCONSISTENT")]
    [InlineData("NOT_PROCESSING")]
    [InlineData("LEASE_EXPIRED")]
    [InlineData("RECORD_MISSING")]
    [InlineData("ADMISSION_MISMATCH")]
    [InlineData("ATTEMPT_MISSING")]
    [InlineData("ATTEMPT_MISMATCH")]
    [InlineData("LEASE_MISMATCH")]
    [InlineData("ORIGINAL_SCORE_MISMATCH")]
    [InlineData("INVALID_PASS_EXPIRATION")]
    [InlineData("ALREADY_ACTIVE")]
    [InlineData("ACTIVE_ID_EXISTS")]
    [InlineData("TOKEN_EXISTS")]
    public void DocumentedRejection_HasSpecificCompatibleException(string status)
    {
        var error = ParseError(RedisResult.Create(new RedisValue[] { status }));
        Assert.IsType<AdmissionCommitRejectedException>(error);
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal($"Admission commit rejected: {status}", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UnknownOrMalformedResponse_RemainsProtocolError(int fields)
    {
        var error = ParseError(RedisResult.Create(Enumerable.Repeat((RedisValue)"UNKNOWN_STATUS", fields).ToArray()));
        Assert.IsType<InvalidOperationException>(error);
    }

    private static Exception ParseError(RedisResult result)
    {
        var error = Record.Exception(() => RedisAdmissionResponses.EnsureCommitSucceeded(result));
        Assert.NotNull(error);
        return error;
    }

    [Fact]
    public async Task ProtocolFailure_PropagatesWithoutDiscardingPendingIdentityTokenOrExpiry()
    {
        var c = new Phase03Context(fixture, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var observed = new ObservedClaimStore(c.Store)
        {
            BeforeCommit = (_, _) => throw new InvalidOperationException("Malformed dependency response.")
        };
        using var worker = c.Worker(new CallbackNotifier(_ => Task.CompletedTask), observed);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessBatchAsync());
        Assert.Equal("Malformed dependency response.", error.Message);
        var prepared = Assert.Single(observed.Commits);
        observed.BeforeCommit = null;

        await worker.ProcessBatchAsync();
        Assert.Single(observed.Ids);
        Assert.Equal(1, observed.StatusCalls);
        Assert.Equal(2, observed.Commits.Count);
        Assert.All(observed.Commits, commit => Assert.Equal(prepared, commit));
        Assert.Equal(prepared.Token, await c.Store.GetPassAsync("user"));
    }
}
