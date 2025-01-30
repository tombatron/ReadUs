using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public class RedisClusterDatabase : RedisDatabase
{
    public RedisClusterDatabase(IRedisConnection connection, RedisClusterConnectionPool pool) : base(connection, pool)
    {
    }

    public override Task<Result<BlockingPopResult>> BlockingLeftPopAsync(params RedisKey[] keys)
    {
        return BlockingLeftPopAsync(TimeSpan.MaxValue, keys);
    }

    public override async Task<Result<BlockingPopResult>> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys)
    {
        if(IsDisposed(out var error))
        {
            return Result<BlockingPopResult>.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateBlockingLeftPopCommand(keys, timeout);

        var rawResult = await Connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult<BlockingPopResult>(ok.Value, (pr) => Result<BlockingPopResult>.Ok((BlockingPopResult)pr)),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message),
            _ => Result<BlockingPopResult>.Error("An unexpected error occurred while attempting to parse the result of the BLPOP command.")
        };

        return result;
    }

    public override Task<Result<BlockingPopResult>> BlockingRightPopAsync(params RedisKey[] keys) => 
        BlockingRightPopAsync(TimeSpan.MaxValue, keys);

    public override async Task<Result<BlockingPopResult>> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys)
    {
        if(IsDisposed(out var error))
        {
            return Result<BlockingPopResult>.Error(error!);
        }
        
        var command = RedisCommandEnvelope.CreateBlockingRightPopCommand(keys, timeout);

        var rawResult = await Connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult<BlockingPopResult>(ok.Value, (pr) => Result<BlockingPopResult>.Ok((BlockingPopResult)pr)),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message),
            _ => Result<BlockingPopResult>.Error("An unexpected error occurred while attempting to parse the result of the BRPOP command.")
        };

        return result;
    }

    public override Task<Result> SelectAsync(int databaseId, CancellationToken cancellationToken = default)
    {
        if (IsDisposed(out var error))
        {
            return Task.FromResult(Result.Error(error!));
        }

        // No-Op, this command doesn't really do anything on clusters. 

        return Task.FromResult(Result.Ok);
    }

    public override async Task<Result> SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues,
        CancellationToken cancellationToken = default)
    {
        var keyGroups = keysAndValues.GroupBy(x => x.Key.Slot);

        // TODO: I don't like this. I think the default behavior should be to return an error if the keys
        //       don't belong to the same slot. Should we support setting keys on multiple slots? Sure I guess, 
        //       but if we do that, I think we could be a bit smarter about it than what we're doing here. 
        
        var setMultipleTasks = new List<Task<Result>>();

        foreach (var keyGroup in keyGroups)
        {
            setMultipleTasks.Add(base.SetMultipleAsync(keyGroup.ToArray(), cancellationToken));
        }

        await Task.WhenAll(setMultipleTasks);
        
        // I don't like this...
        foreach (var setTask in setMultipleTasks)
        {
            var setResult = await setTask;

            if (setResult is Ok)
            {
                // No - Op, dumb.
            }
            
            if (setResult is Error err)
            {
                return err;
            }
        }
        
        return Result.Ok;
    }
}