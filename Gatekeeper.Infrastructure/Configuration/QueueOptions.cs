namespace Gatekeeper.Infrastructure.Configuration;

public class QueueOptions
{
    public const string SectionName = "QueueSettings";

    public int BatchSize { get; set; } = 50;

    public int ReleaseIntervalSeconds { get; set; } = 60;

    public TimeSpan ReleaseInterval => TimeSpan.FromSeconds(ReleaseIntervalSeconds);
}