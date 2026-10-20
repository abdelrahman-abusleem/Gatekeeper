using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddInfrastructure(configuration, addWorker: true);

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool addWorker)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<QueueOptions>()
            .Bind(configuration.GetSection(QueueOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidRetentionPolicy(),
                "Attempt acceptance must be 1..3600 seconds and grace must be 1..300 seconds.")
            .Validate(options => options.HasValidRedisKeys(),
                "Redis keys must be disjoint and contain the same explicit nonempty Cluster hash tag.")
            .ValidateOnStart();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(HasValidSigningKey,
                "JWT signing key must be valid Base64 containing at least 32 bytes.")
            .ValidateOnStart();

        var redisConnectionString = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));

        services.TryAddSingleton<IWaitingQueue, RedisWaitingQueue>();

        services.TryAddSingleton<IAdmissionTokenService, JwtAdmissionTokenService>();

        services.TryAddSingleton<IAdmissionNotifier, RedisAdmissionNotifier>();

        if (addWorker) services.AddHostedService<QueueProcessorWorker>();

        services.TryAddSingleton<IAdmissionClaimStore, RedisAdmissionClaimStore>();
        services.TryAddSingleton<IAdmissionValidator>(provider => provider.GetRequiredService<IAdmissionClaimStore>());
        services.TryAddSingleton<IAdmissionReader>(provider => provider.GetRequiredService<IAdmissionClaimStore>());
        services.TryAddSingleton<IAdmissionReleaser>(provider => provider.GetRequiredService<IAdmissionClaimStore>());
        services.TryAddSingleton<IAdmissionRecovery>(provider => provider.GetRequiredService<IAdmissionClaimStore>());

        return services;
    }

    private static bool HasValidSigningKey(JwtOptions options)
    {
        try { return Convert.FromBase64String(options.SecretKey).Length >= 32; }
        catch (FormatException) { return false; }
    }
}
