using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gatekeeper.AspNetCore;

public static class GatekeeperAdmissionExtensions
{
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
