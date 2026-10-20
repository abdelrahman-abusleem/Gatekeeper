using Gatekeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class Phase05ConfigurationTests
{
    [Theory]
    [InlineData("MaxConcurrentAdmissions", "0")]
    [InlineData("MaxConcurrentAdmissions", "65")]
    [InlineData("MaxWaitingUsers", "-1")]
    [InlineData("WorkerRetryMaxDelaySeconds", "0")]
    [InlineData("WorkerRetryMaxDelaySeconds", "86401")]
    public void InvalidOptions_AreRejectedThroughExistingStartupValidation(string setting, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [$"QueueSettings:{setting}"] = value }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddInfrastructure(config);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<QueueOptions>>().Value);
    }

    [Fact]
    public void Defaults_PreserveSequentialUnlimitedBehavior()
    {
        var options = new QueueOptions();
        Assert.Equal(1, options.MaxConcurrentAdmissions);
        Assert.Equal(0, options.MaxWaitingUsers);
        Assert.Equal(300, options.WorkerRetryMaxDelaySeconds);
        Assert.Equal(60, options.ReleaseIntervalSeconds);
        Assert.Equal(300, options.ClaimIdAcceptanceWindowSeconds);
        Assert.Equal(60, options.ClaimAttemptGraceSeconds);
    }
}
