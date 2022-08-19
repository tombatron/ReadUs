using System;
using System.Threading.Tasks;

namespace ReadUs
{
    // TODO: Start working on a base type that will combine the existing "database" abstractions (cluster and singleinstance)
    //       in such a way that we won't need the Connection classes as well. 
    public abstract class RedisDatabase : IRedisDatabase
    {
        public Task<BlockingPopResult> BlockingLeftPopAsync(params string[] key)
        {
            throw new NotImplementedException();
        }

        public Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params string[] key)
        {
            throw new NotImplementedException();
        }

        public Task<BlockingPopResult> BlockingRightPopAsync(params string[] key)
        {
            throw new NotImplementedException();
        }

        public Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params string[] key)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public Task<string> GetAsync(string key)
        {
            throw new NotImplementedException();
        }

        public Task<int> LeftPushAsync(string key, params string[] element)
        {
            throw new NotImplementedException();
        }

        public Task<int> ListLengthAsync(string key)
        {
            throw new NotImplementedException();
        }

        public Task<int> RightPushAsync(string key, params string[] element)
        {
            throw new NotImplementedException();
        }

        public Task SelectAsync(int databaseId)
        {
            throw new NotImplementedException();
        }

        public Task SetAsync(string key, string value)
        {
            throw new NotImplementedException();
        }
    }
}