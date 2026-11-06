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
            var configuration = new ConfigurationBuilder().Build();

            Assert.Same(services, services.AddInfrastructure(configuration));
            Assert.Same(services, services.AddGatekeeper(configuration, addWorker: false));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IWaitingQueue));
        }
    }
}
