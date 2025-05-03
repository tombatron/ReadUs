using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public class RedisSingleInstanceDatabase(RedisConnectionPool pool) : RedisDatabase(pool)
{
    public override async Task<Result> SelectAsync(int databaseId, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreateSelectCommand(databaseId);

        var rawResult = await Execute(command, cancellationToken).ConfigureAwait(false);

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
        var command = RedisCommandEnvelope.CreateBlockingLeftPopCommand(keys, timeout);

        var rawResult = await Execute(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult<BlockingPopResult>(ok.Value, (pr) => Result<BlockingPopResult>.Ok((BlockingPopResult)pr)),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message),
            _ => Result<BlockingPopResult>.Error("An unexpected error occurred while attempting to parse the result of the BLPOP command.")
        };

        return result;
    }

    public override async Task<Result<BlockingPopResult>> BlockingRightPopAsync(params RedisKey[] keys) =>
        await BlockingRightPopAsync(TimeSpan.MaxValue, keys).ConfigureAwait(false);
    
    public override async Task<Result<BlockingPopResult>> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys)
    {
        var command = RedisCommandEnvelope.CreateBlockingRightPopCommand(keys, timeout);

        var rawResult = await Execute(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult<BlockingPopResult>(ok.Value, (pr) => Result<BlockingPopResult>.Ok((BlockingPopResult)pr)),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message),
            _ => Result<BlockingPopResult>.Error("An unexpected error occurred while attempting to parse the result of the BRPOP command.")
        };

        return result;
    }
}