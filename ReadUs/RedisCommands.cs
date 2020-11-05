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
    public class RedisCommands : IRedisCommands, IDisposable
    {
        private readonly IReadUsConnection _connection;
        private readonly RedisCommandsPool _pool;

        public RedisCommands(IReadUsConnection connection, RedisCommandsPool pool)
        {
            _connection = connection;
            _pool = pool;
        }

        public async Task SelectAsync(int databaseId)
        {
            var rawCommand = Encode(Select, databaseId);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public async Task<string> GetAsync(string key)
        {
            var rawCommand = Encode(Get, key);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return result.ToString();
        }

        public async Task SetAsync(string key, string value)
        {
            var rawCommand = Encode(Set, key, value);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public Task<BlockingPopResult> BlPopAsync(params string[] key) =>
            BlPopAsync(TimeSpan.MaxValue, key);

        public async Task<BlockingPopResult> BlPopAsync(TimeSpan timeout, params string[] key)
        {
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
            var parameters = CombineParameters(BrPop, key, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public async Task<int> LPushAsync(string key, params string[] element)
        {
            var parameters = CombineParameters(LPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> RPushAsync(string key, params string[] element)
        {
            var parameters = CombineParameters(RPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public async Task<int> LlenAsync(string key)
        {
            var rawCommand = Encode(Llen, key);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public void Dispose()
        {
            /*
             * Something to think about here. Once the connection is returned to the pool it could be
             * used by another instance of RedisCommands. But there's nothing really stopping someone
             * from disposing of this instance and then continuing to use it is there?
             */
            _pool.ReturnConnection(_connection);
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