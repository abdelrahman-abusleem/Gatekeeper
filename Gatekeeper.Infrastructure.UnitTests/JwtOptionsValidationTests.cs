using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public class JwtOptionsValidationTests
{
    [Fact]
    public async Task MissingSecret_ShouldFailOnHostStart()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = string.Empty,
                ["JwtSettings:Issuer"] = "TicketingPlatform",
                ["JwtSettings:Audience"] = "TicketingClients"
            });

        builder.Services.AddInfrastructure(builder.Configuration);

        builder.Services.RemoveAll<IHostedService>();

        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());
    }

    [Fact]
    public async Task WeakSecret_ShouldFailOnHostStart()
    {
        var weakSecret = Convert.ToBase64String(new byte[16]);

        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = weakSecret,
                ["JwtSettings:Issuer"] = "TicketingPlatform",
                ["JwtSettings:Audience"] = "TicketingClients"
            });

        builder.Services.AddInfrastructure(builder.Configuration);

        builder.Services.RemoveAll<IHostedService>();

        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(
                "at least 32 bytes",
                StringComparison.Ordinal));
    }
}