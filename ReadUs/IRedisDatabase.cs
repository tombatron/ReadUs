using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisDatabase
{
    Task<Result<byte[]>> Execute(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
    
    Task<Result<int>> RightPushAsync(RedisKey key, params string[] element);

    Task<Result<int>> ListLengthAsync(RedisKey key);
    
    Task<Result<int>> Publish(string channel, string message, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> Subscribe(string channel, Action<string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> Subscribe(string[] channels, Action<string, string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> SubscribeWithPattern(string channelPattern, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> SubscribeWithPattern(string[] channelPatterns, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default);
}