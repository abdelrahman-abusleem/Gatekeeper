using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Gatekeeper.Domain.ValueObjects;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorJwtTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(301, false)]
    public async Task JwtLifetime_ToleratesSlightlyEarlyNotBefore_AndKeepsExclusiveExpiry(int elapsed, bool valid)
    {
        var issue = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10).UtcDateTime;
        var service = new JwtAdmissionTokenService(Options.Create(Settings()),
            NullLogger<JwtAdmissionTokenService>.Instance, new FixedClock(new DateTimeOffset(issue.AddSeconds(elapsed))));
        var token = service.GenerateToken(new(AdmissionId.New(), "user", issue, issue.AddSeconds(300)));
        Assert.Equal(valid, await service.ValidateTokenAsync(token) is not null);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task ValidatorClockDrift_DeniesRatherThanBypassingRedis(int offset)
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var options = Settings();
        options.Issuer = "phase03"; options.Audience = "phase03-clients";
        var validator = new JwtAdmissionTokenService(Options.Create(options),
            NullLogger<JwtAdmissionTokenService>.Instance, new FixedClock(DateTimeOffset.UtcNow.AddSeconds(offset)));
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            NullLogger<RedisAdmissionClaimStore>.Instance, validator);
        Assert.Equal(AdmissionValidationStatus.InvalidToken, await store.ValidateAdmissionAsync(token, "user"));
        Assert.Equal(AdmissionStatus.Active, await c.Store.GetStatusAsync("user"));
    }

    [Fact]
    public async Task SharedPolicy_IsSafeForConcurrentValidation()
    {
        var service = new JwtAdmissionTokenService(Options.Create(Settings()), NullLogger<JwtAdmissionTokenService>.Instance);
        var issue = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1).UtcDateTime;
        var token = service.GenerateToken(new(AdmissionId.New(), "user", issue, issue.AddMinutes(5)));
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => service.ValidateTokenAsync(token)));
        Assert.All(results, result => Assert.NotNull(result));
    }

    [Fact]
    public async Task InvalidInput_DoesNotProduceAttackerControlledWarningVolume()
    {
        var logger = new Levels();
        var service = new JwtAdmissionTokenService(Options.Create(Settings()), logger);
        for (int i = 0; i < 20; i++) Assert.Null(await service.ValidateTokenAsync("invalid-input"));
        Assert.DoesNotContain(logger.Values, level => level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task NonUtcTokenData_IsRejectedBeforeAuthoritativeValidation(DateTimeKind kind)
    {
        var c = new Phase03Context(fixture);
        var (_, token, _) = await Phase04PollingTests.ActivateAsync(c);
        var tokens = new NonUtcTokens(c.Tokens, kind);
        var store = new RedisAdmissionClaimStore(fixture.Connection, Options.Create(c.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisAdmissionClaimStore>.Instance, tokens);
        Assert.Equal(AdmissionValidationStatus.InvalidToken, await store.ValidateAdmissionAsync(token, "user"));
        Assert.Null(await store.GetPassAsync("user"));
    }

    internal static Gatekeeper.Infrastructure.Configuration.JwtOptions Settings() => new()
    {
        SecretKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()),
        Issuer = "refactor-jwt",
        Audience = "refactor-clients"
    };

    private sealed class NonUtcTokens(IAdmissionTokenService inner, DateTimeKind kind) : IAdmissionTokenService
    {
        public string GenerateToken(AdmissionTokenData data) => inner.GenerateToken(data);
        public async Task<AdmissionTokenData?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            var data = await inner.ValidateTokenAsync(token, cancellationToken);
            return data is null ? null : data with { IssuedAtUtc = DateTime.SpecifyKind(data.IssuedAtUtc, kind) };
        }
    }

    private sealed class Levels : ILogger<JwtAdmissionTokenService>
    {
        public List<LogLevel> Values { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Values.Add(level);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
