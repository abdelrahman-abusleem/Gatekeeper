using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RefactorStartupTests
{
    [Fact]
    public void UnavailableRedis_IsNotContactedDuringRegistration_AndFailsOnResolution()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var configuration = Configuration($"127.0.0.1:{port},connectTimeout=100,connectRetry=0,abortConnect=true");
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure(configuration, addWorker: false);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Single(services, service => service.ServiceType == typeof(IConnectionMultiplexer));
        Assert.Throws<RedisConnectionException>(() => provider.GetRequiredService<IConnectionMultiplexer>());
    }

    [Fact]
    public void InvalidExplicitRedisConfiguration_IsNotReplacedByLocalhost()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddInfrastructure(Configuration("unsupported-option=true"), addWorker: false);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<IConnectionMultiplexer>());
    }

    private static IConfiguration Configuration(string redis) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = redis }).Build();
}
