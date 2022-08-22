using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisDatabase : IDisposable
    {
        Task SelectAsync(int databaseId, CancellationToken cancellationToken = default);

        Task<string> GetAsync(RedisKey key, CancellationToken cancellationToken = default);

        Task SetAsync(RedisKey key, string value, CancellationToken cancellationToken = default);

        Task<BlockingPopResult> BlockingLeftPopAsync(params RedisKey[] keys);

        Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys);

        Task<BlockingPopResult> BlockingRightPopAsync(params RedisKey[] keys);

        Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys);

        Task<int> LeftPushAsync(RedisKey key, params string[] element);

        Task<int> RightPushAsync(RedisKey key, params string[] element);

        Task<int> ListLengthAsync(RedisKey key);
    }
}
