using System.Net;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gatekeeper.AspNetCore.IntegrationTests;

public sealed class FinalPassNarrowValidatorTests
{
    [Theory]
    [InlineData(AdmissionValidationStatus.Valid, HttpStatusCode.OK)]
    [InlineData(AdmissionValidationStatus.Inactive, HttpStatusCode.Forbidden)]
    public async Task Authorization_RequiresOnlyValidatorCapability(AdmissionValidationStatus outcome, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("TestHost").AddScheme<AuthenticationSchemeOptions, TestHostAuthentication>("TestHost", _ => { });
        var validator = new ValidatorOnly(outcome);
        builder.Services.AddSingleton<IAdmissionValidator>(validator);
        builder.Services.AddGatekeeperAdmission(options => options.UserIdResolver = principal => principal.FindFirst("host_id")?.Value);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok()).RequireGatekeeperAdmission();
        await app.StartAsync();
        try
        {
            Assert.Null(app.Services.GetService<IAdmissionClaimStore>());
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
            request.Headers.Add("X-Test-User", "trusted-user");
            request.Headers.Add("X-Gatekeeper-Admission", "test-token");
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal(1, validator.Calls);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ValidatorOnly(AdmissionValidationStatus outcome) : IAdmissionValidator
    {
        public int Calls;
        public Task<AdmissionValidationStatus> ValidateAdmissionAsync(string signedToken, string expectedUserId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("test-token", signedToken);
            Assert.Equal("trusted-user", expectedUserId);
            Assert.True(cancellationToken.CanBeCanceled);
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(outcome);
        }
    }
}
