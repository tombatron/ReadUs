using ReadUs.ResultModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public class RedisClusterDatabase : RedisDatabase
{
    public RedisClusterDatabase(IRedisConnection connection, RedisClusterConnectionPool pool) : base(connection, pool)
    {
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

    public override Task SelectAsync(int databaseId, CancellationToken cancellationToken = default)
    {
        CheckIfDisposed();

        // No-Op, this command doesn't really do anything on clusters. 

        return Task.CompletedTask;
    }

    public override async Task SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues, CancellationToken cancellationToken = default)
    {
        var keyGroups = keysAndValues.GroupBy(x => x.Key.Slot);

        var setMultipleTasks = new List<Task>();

        foreach(var keyGroup in keyGroups)
        {
            setMultipleTasks.Add(base.SetMultipleAsync(keyGroup.ToArray(), cancellationToken));
        }

        await Task.WhenAll(setMultipleTasks);            
    }
}