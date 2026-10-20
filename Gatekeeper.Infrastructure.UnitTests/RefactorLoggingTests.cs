using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorLoggingTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData("normal")]
    [InlineData("recovery")]
    [InlineData("unresolved")]
    public async Task Diagnostics_AllowAdmissionIdsOnlyAtDebug_AndNeverExposeUserOrBearerData(string scenario)
    {
        const string user = "private-diagnostic-user";
        var logs = new List<(LogLevel Level, string Message)>();
        var sensitive = new List<string> { user };
        var admissionIds = new List<string>();
        var c = new Phase03Context(fixture, batchSize: 1);
        var secret = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        sensitive.Add(secret);
        var tokens = new JwtAdmissionTokenService(Options.Create(new JwtOptions
        {
            SecretKey = secret,
            Issuer = "phase03",
            Audience = "phase03-clients"
        }), new Capture<JwtAdmissionTokenService>(logs));
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            new Capture<RedisAdmissionClaimStore>(logs), tokens);
        var queue = new RedisWaitingQueue(fixture.Connection, Options.Create(c.Options), new Capture<RedisWaitingQueue>(logs));
        await queue.EnqueueAsync(user);

        if (scenario == "unresolved")
        {
            var observed = new ObservedClaimStore(store)
            {
                AfterCommit = (_, _) => Task.FromException(new TimeoutException("Lost response.")),
                Status = (_, _) => Task.FromResult<AdmissionCommitStatus?>(AdmissionCommitStatus.Unknown)
            };
            using var worker = new QueueProcessorWorker(observed, tokens, new CallbackNotifier(_ => Task.CompletedTask),
                Options.Create(c.Options), new Capture<QueueProcessorWorker>(logs));
            await Assert.ThrowsAsync<TimeoutException>(() => worker.ProcessBatchAsync());
            await worker.ProcessBatchAsync();
            var committed = Assert.Single(observed.Commits);
            sensitive.Add(committed.Token);
            admissionIds.Add(committed.Claim.AdmissionId.ToString());
        }
        else
        {
            if (scenario == "normal")
            {
                Assert.True(await queue.RemoveAsync(user));
                await queue.EnqueueAsync(user);
            }
            var claim = (await store.ClaimNextAsync(AdmissionId.New(), 1, c.Options.ProcessingLease)).Claim!;
            admissionIds.Add(claim.AdmissionId.ToString());
            if (scenario == "recovery")
            {
                await c.ExpireLeaseAsync(claim);
                await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, user, "invalid-json");
                Assert.Equal(AdmissionRecoveryStatus.Inconsistent, Assert.Single(await store.RecoverExpiredAsync(1)).Status);
            }
            else
            {
                var issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1).UtcDateTime;
                var expiry = issued.AddMinutes(5);
                var token = tokens.GenerateToken(new AdmissionTokenData(claim.AdmissionId, user, issued, expiry));
                sensitive.Add(token);
                await store.CommitAsync(claim, token, expiry);
                Assert.Equal(token, await store.GetPassAsync(user));
                await store.ReleaseAsync(user, claim.AdmissionId);
                await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => store.ClaimNextAsync(claim.AdmissionId, 1, c.Options.ProcessingLease));
                var oldId = new AdmissionId(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-10)));
                admissionIds.Add(oldId.ToString());
                await Assert.ThrowsAsync<AdmissionClaimUnavailableException>(() => store.ClaimNextAsync(oldId, 1, c.Options.ProcessingLease));
            }
        }
        Assert.NotEmpty(logs);
        Assert.All(logs, log => Assert.All(sensitive, value => Assert.DoesNotContain(value, log.Message)));
        Assert.All(logs.Where(log => log.Level != LogLevel.Debug),
            log => Assert.All(admissionIds, id => Assert.DoesNotContain(id, log.Message)));
        if (scenario != "recovery")
            Assert.Contains(logs, log => log.Level == LogLevel.Debug && admissionIds.Any(id => log.Message.Contains(id)));
    }

    private sealed class Capture<T>(List<(LogLevel Level, string Message)> logs) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            logs.Add((level, formatter(state, exception)));
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
                logs.AddRange(properties.Select(property => (level, property.Value?.ToString() ?? "")));
        }
    }
}
