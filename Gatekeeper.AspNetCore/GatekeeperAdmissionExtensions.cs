using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gatekeeper.AspNetCore;

/// <summary>Admission authorization registration and endpoint protection alongside host authentication.</summary>
public static class GatekeeperAdmissionExtensions
{
    /// <summary>Register admission authorization alongside the host authentication scheme.</summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="configure">Configure a trusted identity resolver and optional HTTP header name.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">The service collection or callback is null.</exception>
    /// <remarks>Invalid options fail at host startup with OptionsValidationException. The host must register authentication and authorize ownership of queue identities.</remarks>
    public static IServiceCollection AddGatekeeperAdmission(this IServiceCollection services,
        Action<GatekeeperAdmissionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<GatekeeperAdmissionOptions>().Configure(configure)
            .Validate(options => options.UserIdResolver is not null, "A trusted authenticated-user resolver is required.")
            .Validate(options => ValidHeaderName(options.HeaderName), "A valid admission HTTP header name is required.")
            .ValidateOnStart();
        services.AddAuthorization();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, GatekeeperAdmissionHandler>());
        return services;
    }

    /// <summary>Require both the host authorization policy and current Gatekeeper admission for an endpoint.</summary>
    /// <typeparam name="TBuilder">The endpoint convention builder type.</typeparam>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <returns>The same endpoint builder.</returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="InvalidOperationException">Endpoint construction finds AllowAnonymous metadata together with Gatekeeper protection.</exception>
    /// <remarks>Admission is checked through IAdmissionValidator; infrastructure failures deny access. This does not make downstream work atomic with authorization.</remarks>
    public static TBuilder RequireGatekeeperAdmission<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Keep the host's default policy and existing named policies in the composition.
        builder.RequireAuthorization();
        builder.Add(endpoint => endpoint.Metadata.Add(new GatekeeperAdmissionRequirement()));
        builder.Finally(endpoint =>
        {
            if (endpoint.Metadata.OfType<IAllowAnonymous>().Any())
                throw new InvalidOperationException("Gatekeeper admission protection cannot be combined with AllowAnonymous.");
        });
        return builder;
    }

    private static bool ValidHeaderName(string? name) => !string.IsNullOrEmpty(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
}
