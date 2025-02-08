using System;

namespace ReadUs;

public interface IRedisSubscription : IDisposable
{
    void Subscribe(string channel, params string[] otherChannels);

    void Unsubscribe(string channel, params string[] otherChannels);
    
    void PatternSubscribe(string pattern, params string[] otherPatterns);

    void PatternUnsubscribe(string pattern, params string[] otherPatterns);
    
    Result ShardSubscribe(string channel, params string[] otherChannels);

    Result SharedUnsubscribe(string channel, params string[] otherChannels);
    
    void Reset();

    event EventHandler<RedisSubscriptionArgs> MessageReceived;
}