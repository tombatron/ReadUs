using System;
using System.Threading.Tasks;
using ReadUs.Exceptions;
using static ReadUs.Encoder.Encoder;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;
using static ReadUs.ParameterUtilities;
using ReadUs.Parser;
using System.Runtime.CompilerServices;

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

        public async Task<string> GetAsync(string key)
        {
            CheckIfDisposed();

            var rawCommand = Encode(Get, key);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return result.ToString();
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
            CheckIfDisposed();

            // No-Op, this command doesn't really do anything on clusters. 

            return Task.CompletedTask;
        }

        public Task SetAsync(string key, string value)
        {
            throw new NotImplementedException();
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            _pool.ReturnConnection(_connection);

        }

        private void CheckIfDisposed()
        {
            if (_isDisposed)
            {
                throw new RedisDatabaseDisposedException("This instance of `RedisClusterDatabase` has already been disposed.");
            }
        }

        // TODO: Move this and the duplication in `RedisDatabase` to a shared utility class. 
        private static void EvaluateResultAndThrow(ParseResult result, [CallerMemberName] string callingMember = "")
        {
            if (result.Type == ResultType.Error)
            {
                throw new RedisServerException($"Redis returned an error while we were invoking `{callingMember}`. The error was: {result.ToString()}");
            }
        }
    }
}