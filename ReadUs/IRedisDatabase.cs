using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs;

public interface IRedisDatabase : IDisposable
{
    Task<Result> SelectAsync(int databaseId, CancellationToken cancellationToken = default);

    Task<Result<string>> GetAsync(RedisKey key, CancellationToken cancellationToken = default);

    Task<Result> SetAsync(RedisKey key, string value, CancellationToken cancellationToken = default);

    Task<Result<BlockingPopResult>> BlockingLeftPopAsync(params RedisKey[] keys);

    Task<Result<BlockingPopResult>> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys);

    Task<Result<BlockingPopResult>> BlockingRightPopAsync(params RedisKey[] keys);

    Task<Result<BlockingPopResult>> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys);

    Task<Result<int>> LeftPushAsync(RedisKey key, params string[] element);

    Task<Result<int>> RightPushAsync(RedisKey key, params string[] element);

    Task<Result<int>> ListLengthAsync(RedisKey key);

    Task<Result> SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues,
        CancellationToken cancellationToken = default);
    
    Task<Result<int>> Publish(string channel, string message, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> Subscribe(string channel, Action<string> messageHandler, CancellationToken cancellationToken = default);
    
    Task<RedisSubscription> Subscribe(string[] channels, Action<string, string> messageHandler, CancellationToken cancellationToken = default);
}