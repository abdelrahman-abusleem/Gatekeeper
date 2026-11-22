using Gatekeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Gatekeeper.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection
{
    internal static class ConsumerRegistration
    {
        internal static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
            services;
    }
}

namespace Gatekeeper.Infrastructure.UnitTests
{
    public sealed class ServiceRegistrationTests
    {
        [Fact]
        public void GatekeeperRegistration_CoexistsWithConsumerAddInfrastructure()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "localhost:6379" }).Build();

            Assert.Same(services, services.AddInfrastructure(configuration));
            Assert.Same(services, services.AddGatekeeper(configuration, addWorker: false));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IWaitingQueue));
        }
    }
}

namespace Gatekeeper.Infrastructure.UnitTests
{
    public sealed class CodeConfigurationTests(RedisClaimFixture fixture) : IClassFixture<RedisClaimFixture>
    {
        [Fact]
        public void CallbacksApplyCustomSectionsAndPreserveTheHostConnection()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CustomQueue:BatchSize"] = "7",
                ["CustomJwt:Issuer"] = "custom-issuer",
                ["CustomJwt:Audience"] = "custom-audience",
                ["CustomJwt:SecretKey"] = Convert.ToBase64String(new byte[32])
            }).Build();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IConnectionMultiplexer>(fixture.Connection);
            services.AddGatekeeper("unused:6379",
                options => configuration.GetSection("CustomQueue").Bind(options),
                options => configuration.GetSection("CustomJwt").Bind(options), addWorker: false);
            using var provider = services.BuildServiceProvider();
            Assert.Equal(7, provider.GetRequiredService<IOptions<QueueOptions>>().Value.BatchSize);
            Assert.Equal("custom-issuer", provider.GetRequiredService<IOptions<JwtOptions>>().Value.Issuer);
            Assert.Same(fixture.Connection, provider.GetRequiredService<IConnectionMultiplexer>());
            Assert.Empty(provider.GetServices<IHostedService>());
            Assert.NotNull(provider.GetRequiredService<IWaitingQueue>());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void BlankConnectionStringIsRejected(string? connectionString)
        {
            Assert.Throws<ArgumentException>(() => new ServiceCollection().AddGatekeeper(connectionString!, _ => { }, _ => { }));
        }

        [Fact]
        public void NullConfigurationCallbacksAreRejected()
        {
            var services = new ServiceCollection();
            Assert.Throws<ArgumentNullException>(() => services.AddGatekeeper("localhost:6379", null!, _ => { }));
            Assert.Throws<ArgumentNullException>(() => services.AddGatekeeper("localhost:6379", _ => { }, null!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void MissingRedisWithoutHostConnectionIsRejectedAtRegistration(string? connectionString)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = connectionString }).Build();
            var services = new ServiceCollection();
            var error = Assert.Throws<InvalidOperationException>(() => services.AddGatekeeper(configuration));
            Assert.Contains("ConnectionStrings:Redis", error.Message);
            Assert.Throws<InvalidOperationException>(() => services.AddGatekeeper(configuration, addWorker: false));
        }

        [Fact]
        public void MissingRedisWithHostConnectionPreservesTheHostInstance()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConnectionMultiplexer>(fixture.Connection);
            services.AddGatekeeper(new ConfigurationBuilder().Build(), addWorker: false);
            using var provider = services.BuildServiceProvider();
            Assert.Same(fixture.Connection, provider.GetRequiredService<IConnectionMultiplexer>());
        }

        [Fact]
        public async Task InvalidCodeOptionsFailAtStartupWithTheConfigurationPathMessages()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "localhost:6379",
                ["QueueSettings:BatchSize"] = "0",
                ["JwtSettings:Issuer"] = "test",
                ["JwtSettings:Audience"] = "test",
                ["JwtSettings:SecretKey"] = Convert.ToBase64String(new byte[32])
            }).Build();
            var configured = Host.CreateApplicationBuilder();
            configured.Services.AddGatekeeper(configuration, addWorker: false);
            using var configuredHost = configured.Build();
            var expected = await Assert.ThrowsAsync<OptionsValidationException>(() => configuredHost.StartAsync());
            var coded = Host.CreateApplicationBuilder();
            coded.Services.AddGatekeeper("localhost:6379", options => options.BatchSize = 0,
                options => configuration.GetSection("JwtSettings").Bind(options), addWorker: false);
            using var codedHost = coded.Build();
            var actual = await Assert.ThrowsAsync<OptionsValidationException>(() => codedHost.StartAsync());
            Assert.Equal(expected.Failures, actual.Failures);
        }
    }
}
