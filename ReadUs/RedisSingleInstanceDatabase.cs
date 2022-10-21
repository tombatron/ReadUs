using ReadUs.ResultModels;
using System;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.Parser.Parser;

namespace ReadUs
{
    public class RedisSingleInstanceDatabase : RedisDatabase
    {
        public RedisSingleInstanceDatabase(IRedisConnection connection, RedisConnectionPool pool) : base(connection, pool)
        {
        }

        public IRedisConnection UnderlyingConnection => _connection;

        public override async Task SelectAsync(int databaseId, CancellationToken cancellationToken = default)
        {
            CheckIfDisposed();
            
            var command = RedisCommandEnvelope.CreateSelectCommand(databaseId);

            var rawResult = await _connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public override Task<BlockingPopResult> BlockingLeftPopAsync(params RedisKey[] keys) =>
            BlockingLeftPopAsync(TimeSpan.MaxValue, keys);

        public override async Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys)
        {
            CheckIfDisposed();
            
            var command = RedisCommandEnvelope.CreateBlockingLeftPopCommand(keys, timeout);

            var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }

        public override Task<BlockingPopResult> BlockingRightPopAsync(params RedisKey[] keys) =>
            BlockingRightPopAsync(TimeSpan.MaxValue, keys);

        public override async Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys)
        {
            CheckIfDisposed();
            
            var command = RedisCommandEnvelope.CreateBlockingRightPopCommand(keys, timeout);

            var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return (BlockingPopResult)result;
        }
    }
}