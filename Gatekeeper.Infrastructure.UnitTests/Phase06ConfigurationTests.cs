using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase06ConfigurationTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedRegistration_PreservesSharedConnection_AndExplicitWorkerChoice(bool worker)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JwtSettings:SecretKey"] = Convert.ToBase64String(new byte[32]),
            ["JwtSettings:Issuer"] = "test",
            ["JwtSettings:Audience"] = "test"
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConnectionMultiplexer>(fixture.Connection);
        services.AddInfrastructure(config, addWorker: worker);
        services.AddInfrastructure(config, addWorker: worker);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.Same(fixture.Connection, Assert.Single(provider.GetServices<IConnectionMultiplexer>()));
        Assert.Single(provider.GetServices<IAdmissionClaimStore>());
        foreach (var capability in new[] { typeof(IAdmissionValidator), typeof(IAdmissionReader), typeof(IAdmissionReleaser), typeof(IAdmissionRecovery) })
        {
            Assert.Same(provider.GetRequiredService<IAdmissionClaimStore>(), Assert.Single(provider.GetServices(capability)));
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == capability).Lifetime);
        }
        Assert.Single(provider.GetServices<IAdmissionTokenService>());
        Assert.Equal(worker ? 1 : 0, provider.GetServices<IHostedService>().OfType<QueueProcessorWorker>().Count());
        Assert.Equal(Gatekeeper.Domain.Enums.AdmissionStatus.NotFound,
            await provider.GetRequiredService<IAdmissionClaimStore>().GetStatusAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task MandatoryConstructor_RejectsMissingCrypto_AndInputsCancellationRemainExplicit()
    {
        var c = new Phase03Context(fixture);
        var constructor = Assert.Single(typeof(RedisAdmissionClaimStore).GetConstructors());
        Assert.Equal(typeof(IAdmissionTokenService), constructor.GetParameters()[3].ParameterType);
        Assert.Equal(4, constructor.GetParameters().Length);
        Assert.Throws<ArgumentNullException>(() => new RedisAdmissionClaimStore(fixture.Connection,
            Options.Create(c.Options), NullLogger<RedisAdmissionClaimStore>.Instance, null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => c.Store.ValidateAdmissionAsync("token", " "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => c.Store.ReleaseAsync("user", default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Store.ValidateAdmissionAsync("token", "user", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Store.ReleaseAsync("user", Gatekeeper.Domain.ValueObjects.AdmissionId.New(), canceled.Token));
    }
}
