using ReadUs.Exceptions;
using ReadUs.Parser;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;
using static ReadUs.ParameterUtilities;

namespace ReadUs
{
    public class RedisSingleInstanceDatabase : IRedisDatabase
    {
        private readonly IRedisConnection _connection;
        private readonly RedisCommandsPool _pool;

        public RedisSingleInstanceDatabase(IRedisConnection connection, RedisCommandsPool pool)
        {
            _connection = connection;
            _pool = pool;
        }

        public async Task SelectAsync(int databaseId)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(Select, databaseId);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public async Task<string> GetAsync(string key)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(Get, key);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return result.ToString();
        }

        public async Task SetAsync(string key, string value)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(Set, key, value);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public Task<BlockingPopResult> BlockingLeftPopAsync(params string[] key) =>
            BlockingLeftPopAsync(TimeSpan.MaxValue, key);

        public async Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params string[] key)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(BlockingLeftPop, key, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public Task<BlockingPopResult> BlockingRightPopAsync(params string[] key) =>
            BlockingRightPopAsync(TimeSpan.MaxValue, key);

        public async Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params string[] key)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(BlockingRightPop, key, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public async Task<int> LeftPushAsync(string key, params string[] element)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(LeftPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> RightPushAsync(string key, params string[] element)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(RightPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> ListLengthAsync(string key)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(ListLength, key);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            _pool.ReturnConnection(_connection);

            _isDisposed = false;
        }

        private void CheckIfDisposed()
        {
            if (_isDisposed)
            {
                throw new RedisDatabaseDisposedException("This instance of `RedisDatabase` has already been disposed.");
            }
        }

        private static void EvaluateResultAndThrow(ParseResult result, [CallerMemberName] string callingMember = "")
        {
            if (result.Type == ResultType.Error)
            {
                throw new RedisServerException($"Redis returned an error while we were invoking `{callingMember}`. The error was: {result.ToString()}");
            }
        }

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