using System;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;
using static ReadUs.ParameterUtilities;

namespace ReadUs
{
    public class RedisSingleInstanceDatabase : RedisDatabase
    {
        public RedisSingleInstanceDatabase(IRedisConnection connection, RedisCommandsPool pool) : base(connection, pool)
        {
        }

        public override async Task SelectAsync(int databaseId, CancellationToken cancellationToken = default)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(Select, databaseId);

            var rawResult = await _connection.SendCommandAsync(rawCommand, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public override Task<BlockingPopResult> BlockingLeftPopAsync(params RedisKey[] keys) =>
            BlockingLeftPopAsync(TimeSpan.MaxValue, keys);

        public override async Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(BlockingLeftPop, keys, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(keys, rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public override Task<BlockingPopResult> BlockingRightPopAsync(params RedisKey[] keys) =>
            BlockingRightPopAsync(TimeSpan.MaxValue, keys);

        public override async Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys)
        {
            CheckIfDisposed();
            
            var parameters = CombineParameters(BlockingRightPop, keys, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(keys, rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }
    }
}