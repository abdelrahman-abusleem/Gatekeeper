using System.Security.Cryptography;
using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Redis"] = Environment.GetEnvironmentVariable("GATEKEEPER_SMOKE_REDIS") ?? "localhost:6379",
        ["JwtSettings:Issuer"] = "package-smoke",
        ["JwtSettings:Audience"] = "package-smoke-clients",
        ["JwtSettings:SecretKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    });
    builder.Services.AddGatekeeper(builder.Configuration, addWorker: false);
    using var host = builder.Build();
    await host.StartAsync();
    var queue = host.Services.GetRequiredService<IWaitingQueue>();
    var reader = host.Services.GetRequiredService<IAdmissionReader>();
    _ = host.Services.GetRequiredService<IAdmissionValidator>();
    var user = "package-smoke-" + Guid.NewGuid().ToString("N");
    await queue.EnqueueAsync(user);
    if (await reader.GetStatusAsync(user) != AdmissionStatus.Waiting)
        throw new InvalidOperationException("Enqueued user was not Waiting.");
    if (!await queue.RemoveAsync(user))
        throw new InvalidOperationException("Waiting user could not be removed.");
    if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Scripts")))
        throw new InvalidOperationException("A loose Scripts directory was shipped.");
    await host.StopAsync();
    Console.WriteLine("Package smoke test passed.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Package smoke test failed: " + error.Message);
    return 1;
}
