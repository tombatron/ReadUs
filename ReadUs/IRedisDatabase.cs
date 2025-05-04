using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisDatabase
{
    Task<Result<byte[]>> Execute(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> Subscribe(string channel, Action<string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> Subscribe(string[] channels, Action<string, string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> SubscribeWithPattern(string channelPattern, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> SubscribeWithPattern(string[] channelPatterns, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default);
}