using System;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisCommands : IDisposable
    {
        Task SelectAsync(int databaseId);

        Task<string> GetAsync(string key);

        Task SetAsync(string key, string value);

        Task<BlockingPopResult> BlPopAsync(params string[] key);

        Task<BlockingPopResult> BlPopAsync(TimeSpan timeout, params string[] key);
    }
}
