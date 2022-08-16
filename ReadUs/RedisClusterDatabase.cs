using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReadUs
{
    public class RedisClusterDatabase : IRedisDatabase
    {
        private readonly RedisClusterConnection _connection;
        private readonly RedisClusterCommandsPool _pool;

        public RedisClusterDatabase(RedisClusterConnection connection, RedisClusterCommandsPool pool)
        {
            _connection = connection;
            _pool = pool;
        }

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

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}