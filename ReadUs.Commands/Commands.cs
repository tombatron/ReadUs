using ReadUs.Parser;
using Tombatron.Results;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static partial class Commands
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static async Task<Result> Select(this IRedisDatabase @this, int databaseId, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("SELECT", null, null, null, databaseId);
        
        var result = await @this.Execute(command, cancellationToken);
        
        return Parse(result) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SELECT command.")
        };
    }

    public static async Task<Result<string>> Get(this IRedisDatabase @this, RedisKey key, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("GET", null, [key], null, key);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult<string>(ok.Value, ConvertToResultString),
            Error<ParseResult> err => Result<string>.Error(err.Message),
            _ => Result<string>.Error("An unexpected error occurred while attempting to parse the result of the GET command.")
        };
    }

    public static async Task<Result> Set(this IRedisDatabase @this, RedisKey key, string value, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("SET", null, [key], null, key, value);

        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SET command.")
        };
    }

    public static async Task<Result<BlockingPopResult>> BlockingLeftPop(this IRedisDatabase @this, TimeSpan timeout, RedisKey[] keys, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("BLPOP", null, keys, timeout, keys);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ConvertToBlockingPopResult),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message),
            _ => Result<BlockingPopResult>.Error("An unexpected error occurred while attempting to parse the result of the BLPOP command.")
        };
    }
}