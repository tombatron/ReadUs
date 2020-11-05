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

        Task<BlockingPopResult> BrPopAsync(params string[] key);

        Task<BlockingPopResult> BrPopAsync(TimeSpan timeout, params string[] key);

        Task<int> LPushAsync(string key, params string[] element);

        Task<int> RPushAsync(string key, params string[] element);

        Task<int> LlenAsync(string key);
    }
}
