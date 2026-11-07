using Gatekeeper.Infrastructure.Configuration;
using Gatekeeper.Infrastructure.Workers;

namespace Gatekeeper.Infrastructure.UnitTests;

public sealed class RecoveryDelayTests
{
    [Theory]
    [InlineData(3600, 1, 1)]
    [InlineData(1, 3600, 1)]
    [InlineData(5, 5, 5)]
    public void HealthyPass_WaitsForTheShorterInterval(int release, int lease, int seconds)
    {
        var options = new QueueOptions { ReleaseIntervalSeconds = release, ProcessingLeaseSeconds = lease };

        Assert.Equal(TimeSpan.FromSeconds(seconds),
            AdmissionRecoveryLoop.NextDelay(options, new AdmissionRetryPolicy(options), failures: 0));
    }

    [Fact]
    public void FailedPass_UsesExistingExponentialBackoff()
    {
        var options = new QueueOptions { ReleaseIntervalSeconds = 5, ProcessingLeaseSeconds = 1 };
        var retry = new AdmissionRetryPolicy(options);

        Assert.Equal(TimeSpan.FromSeconds(5), AdmissionRecoveryLoop.NextDelay(options, retry, failures: 1));
        Assert.Equal(TimeSpan.FromSeconds(10), AdmissionRecoveryLoop.NextDelay(options, retry, failures: 2));
        Assert.Equal(TimeSpan.FromSeconds(1), AdmissionRecoveryLoop.NextDelay(options, retry, failures: 0));
    }
}
