using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Gatekeeper.Domain.ValueObjects;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase03ConfigurationTests
{
    [Theory]
    [InlineData("QueueKey", "other:{other}:waiting")]
    [InlineData("ProcessingQueueKey", "no-tag:processing")]
    [InlineData("PassKeyPrefix", "prefix:{}:pass:")]
    [InlineData("ClaimAttemptKeyPrefix", "")]
    [InlineData("ActiveAdmissionKeyPrefix", "bad:{}:{admission}:identity:")]
    [InlineData("ActivePassesKey", "gatekeeper:{admission}:waiting")]
    [InlineData("QueueKey", "gatekeeper:{admission}:pass:user")]
    [InlineData("PassKeyPrefix", "gatekeeper:{admission}:claim_attempt:overlap:")]
    public async Task InvalidExternalKeyOverride_FailsOnStartup(string name, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"QueueSettings:{name}"] = value,
            ["JwtSettings:SecretKey"] = Convert.ToBase64String(new byte[32]),
            ["JwtSettings:Issuer"] = "test",
            ["JwtSettings:Audience"] = "test"
        });
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        using var host = builder.Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData("BatchSize", "0")]
    [InlineData("MaxActivePasses", "0")]
    [InlineData("ProcessingLeaseSeconds", "0")]
    [InlineData("ProcessingLeaseSeconds", "3601")]
    [InlineData("PassDurationMinutes", "0")]
    [InlineData("ReleaseIntervalSeconds", "0")]
    [InlineData("ClaimIdAcceptanceWindowSeconds", "0")]
    [InlineData("ClaimIdAcceptanceWindowSeconds", "3601")]
    [InlineData("ClaimAttemptGraceSeconds", "0")]
    [InlineData("ClaimAttemptGraceSeconds", "301")]
    public void InvalidCapacityAndTimingOptions_AreRejected(string name, string value)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"QueueSettings:{name}"] = value
        }).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<QueueOptions>>().Value);
    }

    [Fact]
    public void DangerousLegacyApis_AreAbsent()
    {
        Assert.Null(typeof(IWaitingQueue).GetMethod("DequeueBatchAsync"));
        Assert.Null(typeof(RedisWaitingQueue).GetMethod("DequeueBatchAsync"));
        Assert.Null(typeof(IAdmissionNotifier).GetMethod("DeliverTokenAsync"));
        Assert.Null(typeof(RedisAdmissionNotifier).GetMethod("DeliverTokenAsync"));
    }
}

public sealed class Phase03RedisConfigurationTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Fact]
    public void DefaultAndCustomDynamicKeys_ShareActualComputedHashSlot()
    {
        var custom = new Phase03Context(fixture);
        foreach (var options in new[] { new QueueOptions(), custom.Options })
        {
            Assert.True(options.HasValidRedisKeys());
            var keys = new[] { options.QueueKey, options.ProcessingQueueKey, options.AdmissionRecordsKey,
                options.ActivePassesKey, options.QueueSequenceKey,
                options.ClaimAttemptKeyPrefix + AdmissionId.New(),
                options.PassKeyPrefix + "user:{different}:suffix",
                options.ActiveAdmissionKeyPrefix + "user:{different}:suffix" };
            Assert.Single(keys.Select(key => fixture.Connection.GetHashSlot(key)).Distinct());
        }
    }

    [Fact]
    public void Di_ResolvesCompatibleSingletonsAndWorker_WithExternalKeyOverrides()
    {
        var c = new Phase03Context(fixture);
        var settings = typeof(QueueOptions).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .ToDictionary(p => "QueueSettings:" + p.Name, p => (string?)p.GetValue(c.Options));
        settings["JwtSettings:SecretKey"] = Convert.ToBase64String(new byte[32]);
        settings["JwtSettings:Issuer"] = "test";
        settings["JwtSettings:Audience"] = "test";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddInfrastructure(config);
        services.AddSingleton(fixture.Connection);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        { ValidateOnBuild = true, ValidateScopes = true });
        foreach (var type in new[] { typeof(IWaitingQueue), typeof(IAdmissionClaimStore),
            typeof(IAdmissionTokenService), typeof(IAdmissionNotifier) })
        {
            Assert.Same(provider.GetRequiredService(type), provider.GetRequiredService(type));
            Assert.Equal(ServiceLifetime.Singleton, services.Last(d => d.ServiceType == type).Lifetime);
        }
        Assert.IsType<QueueProcessorWorker>(Assert.Single(provider.GetServices<IHostedService>()));
        Assert.Equal(c.Options.QueueKey, provider.GetRequiredService<IOptions<QueueOptions>>().Value.QueueKey);
        foreach (var file in new[] { "EnqueueUser", "ClaimNextUser", "CommitAdmission", "GetCommitStatus",
            "GetExpiredProcessing", "RecoverAdmission", "GetAdmissionSnapshot", "ReleaseAdmission" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Scripts", file + ".lua")));
    }

    [Fact]
    public async Task SequenceBoundary_PreservesDuplicateRankAndFailsBeforePrecisionLoss()
    {
        var c = new Phase03Context(fixture);
        await c.Db.StringSetAsync(c.Options.QueueSequenceKey, "9007199254740990");
        await c.Queue.EnqueueAsync("last-exact");
        var before = await c.Db.StringGetAsync(c.Options.QueueSequenceKey);
        await c.Queue.EnqueueAsync("last-exact");
        Assert.Equal(before, await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        await Assert.ThrowsAsync<RedisServerException>(() => c.Queue.EnqueueAsync("overflow"));
        Assert.Equal(before, await c.Db.StringGetAsync(c.Options.QueueSequenceKey));
        Assert.Null(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "overflow"));
        var claim = (await c.Store.ClaimNextAsync(AdmissionId.New(), 10, c.Options.ProcessingLease)).Claim!;
        Assert.Equal(9007199254740991, claim.OriginalScore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeClaimOutcome_IsDurableAcrossDelayedRetries(bool capacityFull)
    {
        var c = new Phase03Context(fixture, capacity: 1);
        if (capacityFull) await c.ClaimAsync("reserved");
        var id = AdmissionId.New();
        var first = await c.Store.ClaimNextAsync(id, 1, c.Options.ProcessingLease);
        Assert.Equal(capacityFull ? AdmissionClaimStatus.CapacityFull : AdmissionClaimStatus.QueueEmpty, first.Status);
        if (capacityFull)
        {
            var raw = await c.Db.StringGetAsync(c.Options.ClaimAttemptKeyPrefix + id);
            Assert.Contains("CAPACITY_FULL", raw.ToString());
            // Increase the test limit below to isolate durable negative retry semantics.
        }
        await c.Queue.EnqueueAsync("next");
        var retry = await c.Store.ClaimNextAsync(id, 2, c.Options.ProcessingLease);
        Assert.Equal(first, retry);
        Assert.NotNull(await c.Db.SortedSetScoreAsync(c.Options.QueueKey, "next"));
        Assert.NotNull((await c.Store.ClaimNextAsync(AdmissionId.New(), 2, c.Options.ProcessingLease)).Claim);
    }
}
