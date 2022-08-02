using System;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisDatabase : IDisposable
    {
        Task SelectAsync(int databaseId);

        Task<string> GetAsync(string key);

        Task SetAsync(string key, string value);

        Task<BlockingPopResult> BlockingLeftPopAsync(params string[] key);

        Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params string[] key);

        Task<BlockingPopResult> BlockingRightPopAsync(params string[] key);

        Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params string[] key);

        Task<int> LeftPushAsync(string key, params string[] element);

        Task<int> RightPushAsync(string key, params string[] element);

        Task<int> ListLengthAsync(string key);
    }
}
