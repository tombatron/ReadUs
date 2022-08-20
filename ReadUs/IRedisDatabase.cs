using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisDatabase : IDisposable
    {
        Task SelectAsync(int databaseId);

        Task SelectAsync(int databaseId, CancellationToken cancellationToken);

        Task<string> GetAsync(RedisKey key);

        Task<string> GetAsync(RedisKey key, CancellationToken cancellationToken);

        Task SetAsync(RedisKey key, string value);

        Task SetAsync(RedisKey key, string value, CancellationToken cancellationToken);

        Task<BlockingPopResult> BlockingLeftPopAsync(params RedisKey[] keys);

        Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys);

        Task<BlockingPopResult> BlockingRightPopAsync(params RedisKey[] keys);

        Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys);

        Task<int> LeftPushAsync(RedisKey key, params string[] element);

        Task<int> RightPushAsync(RedisKey key, params string[] element);

        Task<int> ListLengthAsync(RedisKey key);
    }
}
