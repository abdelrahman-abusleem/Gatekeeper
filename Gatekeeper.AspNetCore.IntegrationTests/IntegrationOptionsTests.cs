using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Gatekeeper.AspNetCore.IntegrationTests;

public sealed class IntegrationOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("X Bad")]
    [InlineData("X\r\nInjected")]
    public async Task InvalidHeader_FailsStartup(string? header)
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddGatekeeperAdmission(options =>
        {
            options.HeaderName = header!;
            options.UserIdResolver = _ => "user";
        })).Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task MissingTrustedResolver_FailsStartup()
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddGatekeeperAdmission(_ => { })).Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllowAnonymousConflict_IsRejectedDuringEndpointConstruction(bool anonymousFirst)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        var endpoint = app.MapGet("/conflict", () => "must not execute");
        if (anonymousFirst) endpoint.AllowAnonymous();
        endpoint.RequireGatekeeperAdmission();
        if (!anonymousFirst) endpoint.AllowAnonymous();
        Assert.Throws<InvalidOperationException>(() => ((IEndpointRouteBuilder)app).DataSources.SelectMany(x => x.Endpoints).ToArray());
    }
}
