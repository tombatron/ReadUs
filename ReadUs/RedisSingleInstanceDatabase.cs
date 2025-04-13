using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public class RedisSingleInstanceDatabase(IRedisConnection connection, RedisConnectionPool pool) : RedisDatabase(connection, pool)
{
    // TODO: I'm not sure that this is the right place for this. But that's a matter for another time. 
    public IRedisConnection UnderlyingConnection => connection;

    public override async Task<Result> SelectAsync(int databaseId, CancellationToken cancellationToken = default)
    {
        if (IsDisposed(out var error))
        {
            return Result.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateSelectCommand(databaseId);

        var rawResult = await Connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SELECT command.")
        };

        return result;
    }

    public override Task<Result<BlockingPopResult>> BlockingLeftPopAsync(params RedisKey[] keys) =>
        BlockingLeftPopAsync(TimeSpan.MaxValue, keys);
    
    public override async Task<Result<BlockingPopResult>> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys)
    {
        if (IsDisposed(out var error))
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

    public override Task<Result<BlockingPopResult>> BlockingRightPopAsync(params RedisKey[] keys)
    {
        return BlockingRightPopAsync(TimeSpan.MaxValue, keys);
    }

    public override async Task<Result<BlockingPopResult>> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys)
    {
        if (IsDisposed(out var error))
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
}