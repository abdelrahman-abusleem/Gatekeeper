using System.Reflection;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Exceptions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class FinalPassApiTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public void CapabilityAliases_RespectPreRegisteredCompositeAndReader()
    {
        var c = new Phase03Context(fixture);
        var custom = new ObservedClaimStore(c.Store);
        var reader = new ReaderOnly();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAdmissionClaimStore>(custom);
        services.AddSingleton<IAdmissionReader>(reader);
        var configuration = new ConfigurationBuilder().Build();
        services.AddInfrastructure(configuration, addWorker: false);
        services.AddInfrastructure(configuration, addWorker: false);
        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<IAdmissionClaimStore>());
        Assert.Same(reader, Assert.Single(provider.GetServices<IAdmissionReader>()));
        Assert.Same(custom, Assert.Single(provider.GetServices<IAdmissionValidator>()));
        Assert.Same(custom, Assert.Single(provider.GetServices<IAdmissionReleaser>()));
        Assert.Same(custom, Assert.Single(provider.GetServices<IAdmissionRecovery>()));
        foreach (var capability in typeof(IAdmissionClaimStore).GetInterfaces())
            Assert.All(capability.GetMethods(), method => Assert.True(method.IsAbstract));
        Assert.Null(typeof(IAdmissionTokenService).GetMethod("ValidateToken"));
        var validation = typeof(IAdmissionTokenService).GetMethod("ValidateTokenAsync")!;
        Assert.Equal(typeof(CancellationToken), validation.GetParameters()[1].ParameterType);
        Assert.True(validation.GetParameters()[1].IsOptional);
    }

    [Theory]
    [InlineData("processing", typeof(AdmissionAlreadyProcessingException), "User already has a Processing reservation.")]
    [InlineData("active", typeof(AdmissionAlreadyActiveException), "User already has an active admission pass.")]
    [InlineData("record", typeof(AdmissionRecordConflictException), "User has an unresolved admission record.")]
    public async Task EnqueueRejections_AreTyped_WithoutChangingWaitingOrSequence(string state, Type expected, string message)
    {
        var c = new Phase03Context(fixture);
        if (state == "record") await c.Db.HashSetAsync(c.Options.AdmissionRecordsKey, "user", "unresolved");
        else
        {
            var claim = await c.ClaimAsync();
            if (state == "active") await c.CommitAsync(claim);
        }
        var sequence = await c.Db.StringGetAsync(c.Options.QueueSequenceKey);
        var counts = await c.CountsAsync();
        var error = await Record.ExceptionAsync(() => c.Queue.EnqueueAsync("user"));
        Assert.NotNull(error);
        Assert.Equal(expected, error.GetType());
        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(message, error.Message);
        Assert.Equal(sequence, await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        Assert.Equal(counts, await c.CountsAsync());
    }

    [Theory]
    [InlineData("UNKNOWN", 1)]
    [InlineData("ACTIVE", 2)]
    [InlineData("PROCESSING", 2)]
    public async Task MalformedEnqueueReplies_RemainPlainProtocolFailures(string status, int fields)
    {
        var response = RedisResult.Create(Enumerable.Repeat((RedisValue)status, fields).ToArray());
        var database = DispatchProxy.Create<IDatabase, ReplyProxy>();
        ((ReplyProxy)(object)database).Reply = Task.FromResult(response);
        var connection = DispatchProxy.Create<IConnectionMultiplexer, ReplyProxy>();
        ((ReplyProxy)(object)connection).Reply = database;
        var queue = new RedisWaitingQueue(connection, Options.Create(new QueueOptions()), NullLogger<RedisWaitingQueue>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync("user"));
    }

    [Fact]
    public async Task TokenValidation_PreCanceledTokenRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Phase03Context(fixture).Tokens.ValidateTokenAsync("", cancellation.Token));
    }

    [Fact]
    public async Task StoreReaders_ForwardTheOriginalCancellationToken()
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var tokens = new CapturingTokens(c.Tokens);
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options), NullLogger<RedisAdmissionClaimStore>.Instance, tokens);
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(AdmissionValidationStatus.Valid, await store.ValidateAdmissionAsync(token, "user", cancellation.Token));
        Assert.Equal(token, await store.GetPassAsync("user", cancellation.Token));
        Assert.Equal(2, tokens.Tokens.Count);
        Assert.All(tokens.Tokens, observed => Assert.Equal(cancellation.Token, observed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCrypto_DoesNotBecomeInvalidTokenOrChangeAuthority(bool passRead)
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        using var cancellation = new CancellationTokenSource();
        var tokens = new CapturingTokens(c.Tokens, cancellation.Cancel);
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options), NullLogger<RedisAdmissionClaimStore>.Instance, tokens);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (passRead) await store.GetPassAsync("user", cancellation.Token);
            else await store.ValidateAdmissionAsync(token, "user", cancellation.Token);
        });
        Assert.Equal(cancellation.Token, Assert.Single(tokens.Tokens));
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
    }

    private sealed class ReaderOnly : IAdmissionReader
    {
        public Task<AdmissionStatus> GetStatusAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult(AdmissionStatus.NotFound);
        public Task<string?> GetPassAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class CapturingTokens(IAdmissionTokenService inner, Action? afterValidation = null) : IAdmissionTokenService
    {
        public List<CancellationToken> Tokens { get; } = [];
        public string GenerateToken(AdmissionTokenData data) => inner.GenerateToken(data);
        public async Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            var result = await inner.ValidateTokenAsync(token, cancellationToken);
            afterValidation?.Invoke();
            return result;
        }
    }

    public class ReplyProxy : DispatchProxy
    {
        public object? Reply { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "GetDatabase" or "ScriptEvaluateAsync" => Reply,
            _ => throw new InvalidOperationException("Unexpected proxy call: " + method?.Name)
        };
    }
}
