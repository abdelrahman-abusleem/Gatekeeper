using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Services;
using Gatekeeper.Infrastructure.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of Gatekeeper options, Redis services and the optional Worker.</summary>
public static class DependencyInjection
{
    /// <summary>Register Gatekeeper with configuration binding and a Worker.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="configuration">Configuration containing QueueSettings, JwtSettings and ConnectionStrings:Redis, unless a host multiplexer is already registered.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">The services or a required configuration/callback argument is null.</exception>
    /// <exception cref="InvalidOperationException">ConnectionStrings:Redis is missing or blank and no host IConnectionMultiplexer was registered first.</exception>
    /// <remarks>Options validation runs at host startup (or options resolution) and throws OptionsValidationException for invalid keys, timing, capacity or JWT settings. Redis connects on multiplexer resolution; parse/connection errors propagate then, not during registration. Callback/binding failures propagate when options are evaluated.</remarks>
    public static IServiceCollection AddGatekeeper(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddGatekeeper(configuration, addWorker: true);

    /// <summary>Register Gatekeeper with configuration binding and an optional Worker.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="configuration">Configuration containing QueueSettings, JwtSettings and ConnectionStrings:Redis, unless a host multiplexer is already registered.</param>
    /// <param name="addWorker">Whether to register the background Worker.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">The services or a required configuration/callback argument is null.</exception>
    /// <exception cref="InvalidOperationException">ConnectionStrings:Redis is missing or blank and no host IConnectionMultiplexer was registered first.</exception>
    /// <remarks>Options validation runs at host startup (or options resolution) and throws OptionsValidationException for invalid keys, timing, capacity or JWT settings. Redis connects on multiplexer resolution; parse/connection errors propagate then, not during registration. Callback/binding failures propagate when options are evaluated.</remarks>
    public static IServiceCollection AddGatekeeper(
        this IServiceCollection services,
        IConfiguration configuration,
        bool addWorker)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnectionString)
            && !services.Any(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer)))
            throw new InvalidOperationException("ConnectionStrings:Redis is required unless the host registers IConnectionMultiplexer first.");
        return AddCore(services, redisConnectionString ?? string.Empty,
            options => options.Bind(configuration.GetSection(QueueOptions.SectionName)),
            options => options.Bind(configuration.GetSection(JwtOptions.SectionName)), addWorker);
    }

    /// <summary>Register Gatekeeper with code callbacks and an optional Worker.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="redisConnectionString">Explicit nonblank Redis connection string; host multiplexer registrations are preserved.</param>
    /// <param name="configureQueue">Configure queue values directly or bind any custom configuration section.</param>
    /// <param name="configureJwt">Configure issuer, audience and signing/validation keys directly or bind any custom section.</param>
    /// <param name="addWorker">Whether to register the background Worker; defaults to true.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">The services or a required configuration/callback argument is null.</exception>
    /// <exception cref="ArgumentException">The explicit Redis connection string is null, empty or whitespace.</exception>
    /// <remarks>Options validation runs at host startup (or options resolution) and throws OptionsValidationException for invalid keys, timing, capacity or JWT settings. Redis connects on multiplexer resolution; parse/connection errors propagate then, not during registration. Callback/binding failures propagate when options are evaluated.</remarks>
    public static IServiceCollection AddGatekeeper(
        this IServiceCollection services,
        string redisConnectionString,
        Action<QueueOptions> configureQueue,
        Action<JwtOptions> configureJwt,
        bool addWorker = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(redisConnectionString))
            throw new ArgumentException("A Redis connection string is required.", nameof(redisConnectionString));
        ArgumentNullException.ThrowIfNull(configureQueue);
        ArgumentNullException.ThrowIfNull(configureJwt);
        return AddCore(services, redisConnectionString,
            options => options.Configure(configureQueue), options => options.Configure(configureJwt), addWorker);
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        string redisConnectionString,
        Action<OptionsBuilder<QueueOptions>> configureQueue,
        Action<OptionsBuilder<JwtOptions>> configureJwt,
        bool addWorker)
    {
        var queueOptions = services.AddOptions<QueueOptions>();
        configureQueue(queueOptions);
        queueOptions
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidRetentionPolicy(),
                "Attempt acceptance must be 1..3600 seconds and grace must be 1..300 seconds.")
            .Validate(options => options.HasValidRedisKeys(),
                "Redis keys must be disjoint and contain the same explicit nonempty Cluster hash tag.")
            .ValidateOnStart();

        var jwtOptions = services.AddOptions<JwtOptions>();
        configureJwt(jwtOptions);
        jwtOptions
            .ValidateDataAnnotations()
            .Validate(HasValidSigningKey,
                "JWT signing key must be valid Base64 containing at least 32 bytes.")
            .Validate(JwtSigningKeys.HasValidAdditionalKeys,
                "JWT additional keys must contain at most 4 distinct Base64 keys of at least 32 bytes, excluding the signing key.")
            .ValidateOnStart();

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString);
            if (!redisConnectionString.Split(',').Any(part =>
            {
                var separator = part.IndexOf('=');
                return separator >= 0 && part[..separator].Trim().Equals("abortConnect", StringComparison.OrdinalIgnoreCase);
            }))
                options.AbortOnConnectFail = false;

            options.BacklogPolicy = BacklogPolicy.FailFast;
            return ConnectionMultiplexer.Connect(options);
        });

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
