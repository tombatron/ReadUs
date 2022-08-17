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

        public Task<BlockingPopResult> BlockingLeftPopAsync(params string[] key) =>
            BlockingLeftPopAsync(TimeSpan.MaxValue, key);

        public Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params string[] key)
        {
            // Need to implement multi-key handling. 
            throw new NotImplementedException();

            // CheckIfDisposed();

            // var parameters = CombineParameters(BlockingLeftPop, key, timeout);

            // var rawCommand = Encode(parameters);

            // var rawResult = await _connection.SendCommandAsync(key, rawCommand, timeout).ConfigureAwait(false);

            // var result = Parse(rawResult);

            // EvaluateResultAndThrow(result);

            // return (BlockingPopResult)result;
        }

        public Task<BlockingPopResult> BlockingRightPopAsync(params string[] key) =>
            BlockingRightPopAsync(TimeSpan.MaxValue, key);

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

        public async Task<int> LeftPushAsync(string key, params string[] element)
        {
            CheckIfDisposed();

            var parameters = CombineParameters(LeftPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> ListLengthAsync(string key)
        {
            CheckIfDisposed();

            var rawCommand = Encode(ListLength, key);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> RightPushAsync(string key, params string[] element)
        {
            CheckIfDisposed();

            var parameters = CombineParameters(RightPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public Task SelectAsync(int databaseId)
        {
            CheckIfDisposed();

            // No-Op, this command doesn't really do anything on clusters. 

            return Task.CompletedTask;
        }

        public async Task SetAsync(string key, string value)
        {
            CheckIfDisposed();

            var rawCommand = Encode(Set, key, value);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
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

        // TODO: Move this and the duplication in `RedisDatabase` to a shared utility class. 
        private static int ParseAndReturnInt(ParseResult result, [CallerMemberName] string callingMember = "")
        {
            if (result.Type == ResultType.Integer)
            {
                return int.Parse(result.ToString());
            }
            else
            {
                throw new Exception($"We expected an integer type in the reply but got {result.Type.ToString()} instead.");
            }
        }
    }
}