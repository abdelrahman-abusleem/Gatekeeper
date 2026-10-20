using Gatekeeper.Domain.Exceptions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase07InfrastructureFailureTests
{
    [Fact]
    public async Task ActualServerStopAndRestart_AllOperationsFailExplicitly_ThenSameConnectionRecovers()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var redis = new RedisBuilder("redis:7.0")
            .WithPortBinding(AvailablePort(), 6379)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always").Build();
        await redis.StartAsync(timeout.Token);
        using var connection = await ConnectAsync(redis.GetConnectionString());
        var c = new Phase03Context(connection, capacity: 2);
        var (active, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var pending = await c.ClaimAsync("pending");
        var expired = await c.ExpireLeaseAsync(pending);
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(active));
        var retryId = AdmissionId.New();
        await redis.StopAsync(timeout.Token);

        // Actual process unavailability; no injected Redis exception or shared external endpoint.
        Func<Task>[] calls =
        [
            async () => await c.Store.ClaimNextAsync(retryId, 2, c.Options.ProcessingLease),
            () => c.Store.CommitAsync(expired, "not-committed", DateTime.UtcNow.AddMinutes(5)),
            async () => await c.Store.GetCommitStatusAsync(active, token),
            async () => await c.Store.RecoverAsync(expired),
            async () => await c.Store.RecoverExpiredAsync(2),
            async () => await c.Store.GetStatusAsync("user"),
            async () => await c.Store.GetPassAsync("user"),
            async () => await c.Store.ValidateAdmissionAsync(token, "user"),
            async () => await c.Store.ReleaseAsync("user", active.AdmissionId)
        ];
        foreach (var call in calls)
            await Assert.ThrowsAnyAsync<RedisException>(() => call().WaitAsync(timeout.Token));

        await redis.StartAsync(timeout.Token);
        await EventuallyAsync(async () =>
        {
            try { return await c.Store.ValidateAdmissionAsync(token, "user") == AdmissionValidationStatus.Valid; }
            catch (RedisException) { return false; }
        }, timeout.Token);
        Assert.Equal(token, await c.Store.GetPassAsync("user"));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(active)));
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(active, token));
        Assert.Equal(AdmissionRecoveryStatus.Recovered, await c.Store.RecoverAsync(expired));
        var fresh = (await c.Store.ClaimNextAsync(retryId, 2, c.Options.ProcessingLease)).Claim!;
        Assert.Equal(pending.UserId, fresh.UserId);
        Assert.Equal(pending.OriginalScore, fresh.OriginalScore);
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.Store.CommitAsync(expired, "stale", DateTime.UtcNow.AddMinutes(5)));
        Assert.Equal((0L, 1L, 1L), await c.CountsAsync());
        Assert.Equal(AdmissionReleaseStatus.Released, await c.Store.ReleaseAsync("user", active.AdmissionId));
    }

    [Fact]
    public async Task WorkerResumesOriginalClaimAfterRealOutage_WithoutNewIdentityOrNotificationReplay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var redis = new RedisBuilder("redis:7.0")
            .WithPortBinding(AvailablePort(), 6379)
            .WithCommand("redis-server", "--appendonly", "yes", "--appendfsync", "always").Build();
        await redis.StartAsync(timeout.Token);
        using var connection = await ConnectAsync(redis.GetConnectionString());
        var c = new Phase03Context(connection, batchSize: 1);
        await c.Queue.EnqueueAsync("user");
        var observed = new ObservedClaimStore(c.Store);
        var stopped = false;
        observed.AfterClaim = async (_, ct) =>
        {
            if (stopped) return;
            stopped = true;
            await redis.StopAsync(ct); // Claim is real and durable; Commit now encounters a real outage.
        };
        var notifications = 0;
        using var worker = c.Worker(new CallbackNotifier(_ => { notifications++; return Task.CompletedTask; }), observed);
        await Assert.ThrowsAnyAsync<RedisException>(() => worker.ProcessBatchAsync(timeout.Token, recover: false));
        var original = Assert.Single(observed.Ids);
        var prepared = Assert.Single(observed.Commits);
        Assert.Equal(0, notifications);
        await redis.StartAsync(timeout.Token);
        await EventuallyAsync(async () =>
        {
            try { return await c.Store.GetStatusAsync("user") == AdmissionStatus.Processing; }
            catch (RedisException) { return false; }
        }, timeout.Token);
        await worker.ProcessBatchAsync(timeout.Token, recover: false);
        Assert.Equal(original, Assert.Single(observed.Ids));
        Assert.Equal(2, observed.Commits.Count);
        Assert.All(observed.Commits, entry => Assert.Equal(prepared, entry));
        Assert.Equal(1, notifications);
        Assert.Equal(prepared.Token, await c.Store.GetPassAsync("user"));
        Assert.Equal((0L, 0L, 1L), await c.CountsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualReplicaPromotion_ReportsSurvivingVersusLostAcknowledgedCommit(bool replicateCommit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        const string start = "redis-server --port 6379 --bind 0.0.0.0 --protected-mode no --save '' --appendonly no --daemonize yes --pidfile /tmp/primary.pid\n"
            + "redis-server --port 6380 --bind 0.0.0.0 --protected-mode no --save '' --appendonly no --daemonize yes --replicaof 127.0.0.1 6379\n"
            + "while :; do sleep 1; done";
        await using var topology = new ContainerBuilder("redis:7.0")
            .WithEntrypoint("/bin/sh").WithCommand("-ec", start)
            .WithPortBinding(6379, true).WithPortBinding(6380, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("sh", "-c",
                "redis-cli -p 6380 info replication | grep -q master_link_status:up")).Build();
        await topology.StartAsync(timeout.Token);
        using var primary = await ConnectAsync($"{topology.Hostname}:{topology.GetMappedPublicPort(6379)}");
        using var replica = await ConnectAsync($"{topology.Hostname}:{topology.GetMappedPublicPort(6380)}");
        var c = new Phase03Context(primary, capacity: 1);
        var claim = await c.ClaimAsync();
        Assert.Equal(1, (long)await c.Db.ExecuteAsync("WAIT", 1, 5000));
        var replicaDb = replica.GetDatabase();
        Assert.True(await replicaDb.KeyExistsAsync(c.AttemptKey(claim)));
        if (!replicateCommit) await replicaDb.ExecuteAsync("REPLICAOF", "NO", "ONE");
        var expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300).UtcDateTime;
        var token = c.Tokens.GenerateToken(new(claim.AdmissionId, "user", expiry.AddMinutes(-5), expiry));
        await c.Store.CommitAsync(claim, token, expiry);
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, "user"));
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim));
        if (replicateCommit) Assert.Equal(1, (long)await c.Db.ExecuteAsync("WAIT", 1, 5000));

        var killed = await topology.ExecAsync(["sh", "-c", "kill -9 $(cat /tmp/primary.pid)"], timeout.Token);
        Assert.Equal(0, killed.ExitCode);
        await Assert.ThrowsAnyAsync<RedisException>(() => c.Store.ValidateAdmissionAsync(token, "user"));
        await replicaDb.ExecuteAsync("REPLICAOF", "NO", "ONE");
        var role = (RedisResult[])(await replicaDb.ExecuteAsync("ROLE"))!;
        Assert.Equal("master", role[0].ToString());
        // Explicit endpoint cutover: this is real manual promotion, not Sentinel auto-discovery.
        var successor = new RedisAdmissionClaimStore(replica, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, c.Tokens);
        if (replicateCommit)
        {
            Assert.Equal(AdmissionValidationStatus.Valid, await successor.ValidateAdmissionAsync(token, "user"));
            Assert.Equal(token, await successor.GetPassAsync("user"));
            Assert.Equal(AdmissionCommitStatus.Committed, await successor.GetCommitStatusAsync(claim, token));
            Assert.Equal(deadline, (long)await replicaDb.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
            Assert.Equal(AdmissionReleaseStatus.Released, await successor.ReleaseAsync("user", claim.AdmissionId));
            Assert.Equal(AdmissionCommitStatus.Expired, await successor.GetCommitStatusAsync(claim, token));
            Assert.Equal(deadline, (long)await replicaDb.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        }
        else
        {
            Assert.Equal(AdmissionValidationStatus.Inactive, await successor.ValidateAdmissionAsync(token, "user"));
            Assert.Null(await successor.GetPassAsync("user"));
            Assert.Equal(AdmissionCommitStatus.NotCommitted, await successor.GetCommitStatusAsync(claim, token));
            Assert.Equal(-1, (long)await replicaDb.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
            Assert.Equal(1, await replicaDb.SortedSetLengthAsync(c.Options.ProcessingQueueKey));
            Assert.Equal(0, await replicaDb.SortedSetLengthAsync(c.Options.ActivePassesKey));
        }
    }

    private static Task<ConnectionMultiplexer> ConnectAsync(string endpoint)
    {
        var options = ConfigurationOptions.Parse(endpoint);
        options.AsyncTimeout = 500;
        options.AllowAdmin = true;
        options.ConnectTimeout = 3000;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ReconnectRetryPolicy = new LinearRetry(100);
        return ConnectionMultiplexer.ConnectAsync(options);
    }

    private static int AvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await condition()) return;
            await Task.Delay(50, cancellationToken);
        }
        Assert.Fail("The observable Redis condition did not converge within 15 seconds.");
    }
}
