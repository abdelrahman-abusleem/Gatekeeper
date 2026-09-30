using Gatekeeper.Domain.Abstractions;
using Gatekeeper.Domain.Models;
using Gatekeeper.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Gatekeeper.Infrastructure.Services;

public class RedisWaitingQueue : IWaitingQueue
{
    private readonly IDatabase _database;
    private readonly QueueOptions _options;

    private const string QueueKey = "gatekeeper:waiting_queue";

    public RedisWaitingQueue(IConnectionMultiplexer redis , IOptions<QueueOptions> options)
    {
        _database = redis.GetDatabase();
        _options = options.Value;
    }
}

