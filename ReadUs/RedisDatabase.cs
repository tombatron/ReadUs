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
    public class RedisDatabase : IRedisDatabase, IDisposable
    {
        private readonly IRedisConnection _connection;
        private readonly RedisCommandsPool _pool;

        public RedisDatabase(IRedisConnection connection, RedisCommandsPool pool)
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

        public Task<BlockingPopResult> BlPopAsync(params string[] key) =>
            BlPopAsync(TimeSpan.MaxValue, key);

        public async Task<BlockingPopResult> BlPopAsync(TimeSpan timeout, params string[] key)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(BlPop, key, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public Task<BlockingPopResult> BrPopAsync(params string[] key) =>
            BrPopAsync(TimeSpan.MaxValue, key);

        public async Task<BlockingPopResult> BrPopAsync(TimeSpan timeout, params string[] key)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(BrPop, key, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public async Task<int> LPushAsync(string key, params string[] element)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(LPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> RPushAsync(string key, params string[] element)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(RPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> LlenAsync(string key)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(Llen, key);

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