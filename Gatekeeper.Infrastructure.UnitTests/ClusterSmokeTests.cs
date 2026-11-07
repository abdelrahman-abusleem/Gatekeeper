using Gatekeeper.Domain.Exceptions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class ClusterSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ThreeMasterCluster_RoutesAtomicWorkflowAcrossAllSlotOwners()
    {
        var ports = new List<int>();
        var reservations = new List<TcpListener>();
        try
        {
            // Equal host/container ports allow localhost advertisements for both client and peers.
            while (ports.Count < 3)
            {
                int candidate = Random.Shared.Next(18000, 25000);
                if (ports.Contains(candidate)) continue;
                var listener = new TcpListener(IPAddress.Loopback, candidate);
                try { listener.Start(); }
                catch (SocketException) { continue; }
                ports.Add(candidate);
                reservations.Add(listener);
            }
        }
        finally { foreach (var reservation in reservations) reservation.Stop(); }

        var start = string.Join("\n", ports.Select((port, index) =>
            $"mkdir -p /tmp/gatekeeper/{index}\n" +
            $"redis-server --port {port} --bind 0.0.0.0 --protected-mode no --daemonize yes " +
            $"--dir /tmp/gatekeeper/{index} --logfile /tmp/gatekeeper/{index}/redis.log --save '' --appendonly no --cluster-enabled yes " +
            $"--cluster-config-file nodes.conf --cluster-node-timeout 5000 " +
            $"--cluster-announce-ip 127.0.0.1 --cluster-announce-port {port}"));
        start += "\nwhile :; do sleep 1; done";
        var builder = new ContainerBuilder(TestRedisImage.Name)
            .WithEntrypoint("/bin/sh").WithCommand("-ec", start);
        foreach (var port in ports) builder = builder.WithPortBinding(port, port);
        await using var cluster = builder.Build();
        await StartReadyClusterAsync(cluster, ports);
        var configuration = new ConfigurationOptions { ConnectTimeout = 10000, SyncTimeout = 10000, AllowAdmin = true };
        foreach (var port in ports) configuration.EndPoints.Add("127.0.0.1", port);
        using var connection = await ConnectionMultiplexer.ConnectAsync(configuration);
        foreach (var port in ports)
        {
            var server = connection.GetServer("127.0.0.1", port);
            Assert.Equal(ServerType.Cluster, server.ServerType);
            Assert.Equal("noeviction", Assert.Single(await server.ConfigGetAsync("maxmemory-policy")).Value);
            var info = (await server.ExecuteAsync("CLUSTER", "INFO")).ToString();
            Assert.Contains("cluster_state:ok", info);
            Assert.Contains("cluster_known_nodes:3", info);
            Assert.Contains("cluster_slots_assigned:16384", info);
        }
        // redis-cli assigns the three masters consecutive slot thirds. Exercise every owner.
        for (int owner = 0; owner < 3; owner++)
        {
            AdmissionTestContext context;
            do { context = new AdmissionTestContext(connection, capacity: 2); }
            while (Owner(connection.GetHashSlot(context.Options.QueueKey)) != owner);
            await VerifyWorkflowAsync(context);

            // Kill actual client sockets on this disposable slot owner, retaining all Redis state.
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDisconnect(object? sender, ConnectionFailedEventArgs args) => disconnected.TrySetResult();
            connection.ConnectionFailed += OnDisconnect;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var killed = await cluster.ExecAsync(["redis-cli", "-p", ports[owner].ToString(),
                    "CLIENT", "KILL", "TYPE", "normal", "SKIPME", "yes"], timeout.Token);
                Assert.Equal(0, killed.ExitCode);
                Assert.True(int.Parse(killed.Stdout.Trim()) > 0);
                await disconnected.Task.WaitAsync(timeout.Token);
                Assert.Equal(AdmissionStatus.Active, await context.Store.GetStatusAsync("second"));
            }
            finally { connection.ConnectionFailed -= OnDisconnect; }
        }
        foreach (var port in ports)
            Assert.NotEmpty(connection.GetServer("127.0.0.1", port).Keys(pattern: "phase03:*"));
    }

    private async Task StartReadyClusterAsync(IContainer cluster, IReadOnlyList<int> ports)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var last = "Container not yet started.";
        try
        {
            await cluster.StartAsync(ct);
            // A daemonized redis-server can return to the shell before its socket accepts connections.
            while (true)
            {
                var pings = await Task.WhenAll(ports.Select(port =>
                    cluster.ExecAsync(["redis-cli", "-p", port.ToString(), "PING"], ct)));
                last = string.Join("\n", pings.Select((ping, index) =>
                    $"{ports[index]} PING: {ping.Stdout}{ping.Stderr}"));
                if (pings.All(ping => ping.ExitCode == 0 && ping.Stdout.Trim() == "PONG")) break;
                await Task.Delay(100, ct);
            }

            string[] create = ["redis-cli", "--cluster", "create", .. ports.Select(port => $"127.0.0.1:{port}"),
                "--cluster-replicas", "0", "--cluster-yes"];
            var creation = await cluster.ExecAsync(create, ct);
            last = $"Cluster creation exit {creation.ExitCode}:\n{creation.Stdout}\n{creation.Stderr}";
            output.WriteLine(last);
            if (creation.ExitCode != 0) throw new InvalidOperationException("redis-cli --cluster create failed.");

            while (true)
            {
                var views = await Task.WhenAll(ports.Select(async port =>
                {
                    var info = await cluster.ExecAsync(["redis-cli", "-p", port.ToString(), "CLUSTER", "INFO"], ct);
                    var nodes = await cluster.ExecAsync(["redis-cli", "-p", port.ToString(), "CLUSTER", "NODES"], ct);
                    var fields = info.Stdout.Split('\n', StringSplitOptions.TrimEntries);
                    var owners = nodes.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Where(parts => parts.Length > 8 && parts[2].Split(',').Contains("master"))
                        .Select(parts => $"{parts[0]} {string.Join(" ", parts.Skip(8))}")
                        .Order(StringComparer.Ordinal).ToArray();
                    var ready = info.ExitCode == 0 && nodes.ExitCode == 0 && owners.Length == 3
                        && fields.Contains("cluster_state:ok") && fields.Contains("cluster_known_nodes:3")
                        && fields.Contains("cluster_slots_assigned:16384") && fields.Contains("cluster_slots_ok:16384");
                    return (Ready: ready, Owners: string.Join("\n", owners),
                        State: $"--- node {port} ---\n{info.Stdout}{info.Stderr}{nodes.Stdout}{nodes.Stderr}");
                }));
                last = string.Join("\n", views.Select(view => view.State));
                if (views.All(view => view.Ready && view.Owners == views[0].Owners)) break;
                await Task.Delay(100, ct);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cluster readiness failed. Last observed state:\n{last}\n{await StartupDiagnosticsAsync(cluster, ports)}", ex);
        }

        output.WriteLine(last);
        output.WriteLine(await StartupDiagnosticsAsync(cluster, ports));
    }

    private static async Task<string> StartupDiagnosticsAsync(IContainer cluster, IReadOnlyList<int> ports)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new StringBuilder();
        try
        {
            var (stdout, stderr) = await cluster.GetLogsAsync(ct: deadline.Token);
            logs.AppendLine($"Container stdout:\n{stdout}\nContainer stderr:\n{stderr}");
        }
        catch (Exception ex) { logs.AppendLine($"Container logs unavailable: {ex.Message}"); }

        for (int index = 0; index < ports.Count; index++)
        {
            logs.AppendLine($"--- node {ports[index]} log tail ---");
            try
            {
                // Docker can read the log file even if the startup shell exited.
                var log = await cluster.ReadFileAsync($"/tmp/gatekeeper/{index}/redis.log", deadline.Token);
                logs.AppendLine(string.Join("\n", Encoding.UTF8.GetString(log).Split('\n').TakeLast(25)));
            }
            catch (Exception ex) { logs.AppendLine($"Node log unavailable: {ex.Message}"); }
        }
        return logs.ToString();
    }

    private static int Owner(int slot) => slot <= 5460 ? 0 : slot <= 10922 ? 1 : 2;

    private static async Task VerifyWorkflowAsync(AdmissionTestContext c)
    {
        await c.Queue.EnqueueAsync("first:{suffix}");
        await c.Queue.EnqueueAsync("second");
        Assert.Equal(AdmissionStatus.Waiting, await c.Store.GetStatusAsync("first:{suffix}"));
        var claim = (await c.Store.ClaimNextAsync(AdmissionId.New(), 2, c.Options.ProcessingLease)).Claim!;
        Assert.Equal("first:{suffix}", claim.UserId);
        Assert.Equal(AdmissionStatus.Processing, await c.Store.GetStatusAsync(claim.UserId));
        Assert.Null(await c.Store.GetPassAsync(claim.UserId));
        var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;
        var data = new AdmissionTokenData(claim.AdmissionId, claim.UserId, issued, issued.AddMinutes(5));
        var token = c.Tokens.GenerateToken(data);
        await c.Store.CommitAsync(claim, token, data.ExpiresAtUtc);
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync(claim.UserId));
        Assert.Equal(token, await c.Store.GetPassAsync(claim.UserId));
        Assert.Equal(AdmissionStatus.NotFound, await c.Store.GetStatusAsync("missing:{other}"));
        Assert.Null(await c.Store.GetPassAsync("missing:{other}"));
        var created = Convert.ToInt64(claim.AdmissionId.ToString().Replace("-", "")[..12], 16);
        var passExpiry = new DateTimeOffset(data.ExpiresAtUtc).ToUnixTimeMilliseconds();
        Assert.Equal(Math.Max(passExpiry, created + 300000) + 60000,
            (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        Assert.Equal(AdmissionCommitStatus.Committed, await c.Store.GetCommitStatusAsync(claim, token));
        Assert.Equal(claim.AdmissionId.ToString(), (await c.Db.StringGetAsync(c.IdentityKey(claim.UserId))).ToString());
        var expired = (await c.Store.ClaimNextAsync(AdmissionId.New(), 2, c.Options.ProcessingLease)).Claim!;
        expired = await c.ExpireLeaseAsync(expired);
        Assert.Equal(AdmissionRecoveryStatus.Recovered, Assert.Single(await c.Store.RecoverExpiredAsync(1)).Status);
        Assert.Equal((1L, 0L, 1L), await c.CountsAsync());
        Assert.Equal((double)expired.OriginalScore, await c.Db.SortedSetScoreAsync(c.Options.QueueKey, expired.UserId));
        await Assert.ThrowsAsync<AdmissionCommitRejectedException>(() => c.CommitAsync(expired));
        var fresh = (await c.Store.ClaimNextAsync(AdmissionId.New(), 2, c.Options.ProcessingLease)).Claim!;
        Assert.NotEqual(expired.AdmissionId, fresh.AdmissionId);
        await c.CommitAsync(fresh);
        await c.Queue.EnqueueAsync("third");
        Assert.Equal(AdmissionClaimStatus.CapacityFull,
            (await c.Store.ClaimNextAsync(AdmissionId.New(), 2, c.Options.ProcessingLease)).Status);
        Assert.Equal((1L, 0L, 2L), await c.CountsAsync());
        var stale = UuidRetentionTestSupport.At(await UuidRetentionTestSupport.NowAsync(c.Db) - 3600000);
        await Assert.ThrowsAsync<Gatekeeper.Domain.Exceptions.AdmissionClaimUnavailableException>(() =>
            c.Store.ClaimNextAsync(stale, 2, c.Options.ProcessingLease));
        await VerifyBackpressureAndWorkerAsync(c);
        await VerifyReleaseAndValidationAsync(c, claim, token);
    }

    private static async Task VerifyReleaseAndValidationAsync(AdmissionTestContext c, AdmissionClaim claim, string token)
    {
        Assert.Equal(AdmissionValidationStatus.Valid, await c.Store.ValidateAdmissionAsync(token, claim.UserId));
        var deadline = (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim));
        Assert.Equal(AdmissionReleaseStatus.Released, await c.Store.ReleaseAsync(claim.UserId, claim.AdmissionId));
        Assert.Equal(deadline, (long)await c.Db.ExecuteAsync("PEXPIRETIME", c.AttemptKey(claim)));
        Assert.Equal(AdmissionReleaseStatus.NotCurrent, await c.Store.ReleaseAsync(claim.UserId, claim.AdmissionId));
        Assert.Equal(AdmissionValidationStatus.Inactive, await c.Store.ValidateAdmissionAsync(token, claim.UserId));
        Assert.Equal(AdmissionCommitStatus.Expired, await c.Store.GetCommitStatusAsync(claim, token));
        var fresh = (await c.Store.ClaimNextAsync(AdmissionId.New(), 2, c.Options.ProcessingLease)).Claim!;
        Assert.Equal("third", fresh.UserId);
        await c.Store.ReleaseAsync(claim.UserId, claim.AdmissionId);
        Assert.Equal((0L, 1L, 1L), await c.CountsAsync());
    }

    private static async Task VerifyBackpressureAndWorkerAsync(AdmissionTestContext existing)
    {
        // Reuse the same actual slot owner with a distinct namespace, no production data.
        var options = existing.Options;
        var prefix = options.QueueKey + ":pressure";
        var limitedOptions = new Gatekeeper.Infrastructure.Configuration.QueueOptions
        {
            QueueKey = prefix + ":waiting",
            ProcessingQueueKey = prefix + ":processing",
            AdmissionRecordsKey = prefix + ":records",
            ActivePassesKey = prefix + ":active",
            QueueSequenceKey = prefix + ":sequence",
            ClaimAttemptKeyPrefix = prefix + ":attempt:",
            PassKeyPrefix = prefix + ":pass:",
            ActiveAdmissionKeyPrefix = prefix + ":identity:",
            MaxWaitingUsers = 3,
            MaxConcurrentAdmissions = 2,
            BatchSize = 3,
            MaxActivePasses = 3
        };
        // Context retains the fixture multiplexer; IDatabase exposes it without creating connections.
        var redis = existing.Db.Multiplexer;
        var queue = new Gatekeeper.Infrastructure.Services.RedisWaitingQueue(redis,
            Microsoft.Extensions.Options.Options.Create(limitedOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisWaitingQueue>.Instance);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            try { await queue.EnqueueAsync($"burst:{i}"); return true; }
            catch (Gatekeeper.Domain.Exceptions.WaitingQueueFullException) { return false; }
        }));
        Assert.Equal(3, accepted.Count(x => x));
        Assert.Equal("3", (await existing.Db.StringGetAsync(limitedOptions.QueueSequenceKey)).ToString());
        var store = new Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore(redis,
            Microsoft.Extensions.Options.Options.Create(limitedOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Services.RedisAdmissionClaimStore>.Instance,
            existing.Tokens);
        using var worker = new Gatekeeper.Infrastructure.Workers.QueueProcessorWorker(store, existing.Tokens,
            new CallbackNotifier(_ => Task.CompletedTask), Microsoft.Extensions.Options.Options.Create(limitedOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Gatekeeper.Infrastructure.Workers.QueueProcessorWorker>.Instance);
        await worker.ProcessBatchAsync();
        Assert.Equal(3, await existing.Db.SortedSetLengthAsync(limitedOptions.ActivePassesKey));
        Assert.Equal(0, await queue.GetQueueLengthAsync());
    }
}
