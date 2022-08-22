using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public class RedisClusterDatabase : RedisDatabase
    {
        public RedisClusterDatabase(IRedisConnection connection, RedisClusterCommandsPool pool) : base(connection, pool)
        {
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

            var parameters = CombineParameters(BlockingLeftPop, keys, timeout);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(keys, rawCommand, timeout).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public override Task SelectAsync(int databaseId, CancellationToken cancellationToken = default)
        {
            CheckIfDisposed();

            // No-Op, this command doesn't really do anything on clusters. 

            return Task.CompletedTask;
        }

        public override Task SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues, CancellationToken cacncellationToken = default)
        {
            return Task.CompletedTask;

        }
    }
}