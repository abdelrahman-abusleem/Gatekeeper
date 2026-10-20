using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Services;

public sealed class RedisAdmissionNotifier : IAdmissionNotifier
{
    private readonly ISubscriber _subscriber;
    private readonly QueueOptions _options;

    public RedisAdmissionNotifier(IConnectionMultiplexer redis, IOptions<QueueOptions> options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _subscriber = redis.GetSubscriber();
    }

    public async Task NotifyReadyAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        await _subscriber.PublishAsync(
            RedisChannel.Literal(_options.BuildNotifyChannel(userId)), AdmissionNotificationPayloads.Ready);
    }
}
