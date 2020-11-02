using ReadUs.Parser;
using System;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;

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

            if (result.Type == ResultType.Error)
            {
                throw new Exception("(╯°□°）╯︵ ┻━┻");
            }
        }

        public async Task<string> GetAsync(string key)
        {
            var rawCommand = Encode(Get, key);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            return result.ToString();
        }

        public async Task SetAsync(string key, string value)
        {
            var rawCommand = Encode(Set, key, value);

            var rawResult = await _connection.SendCommandAsync(rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            if (result.Type == ResultType.Error)
            {
                throw new Exception("(╯°□°）╯︵ ┻━┻");
            }
        }

        public Task<BlockingPopResult> BlPopAsync(params string[] key) =>
            BlPopAsync(TimeSpan.MaxValue, key);

        public async Task<BlockingPopResult> BlPopAsync(TimeSpan timeout, params string[] key)
        {
            var parameters = new object[key.Length + 2];

            parameters[0] = BlPop;

            Array.Copy(key, 0, parameters, 1, key.Length);

            parameters[parameters.Length - 1] = 0;

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            if (result.Type == ResultType.Error)
            {
                throw new Exception(result.ToString());
            }

            return (BlockingPopResult)result;
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
    }
}