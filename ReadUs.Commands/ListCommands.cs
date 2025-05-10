using ReadUs.Parser;
using Tombatron.Results;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static partial class Commands
{
    public static async Task<Result<int>> ListLength(this IRedisDatabase @this, RedisKey key, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreateListLengthCommand(key);

        var rawResult = await @this.Execute(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the LLEN command.")
        };

        return result;
    }
    
    public static async Task<Result<int>> LeftPush(this IRedisDatabase @this, RedisKey key, string[] elements, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("LPUSH", null, [key], null, key, elements);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the rresult of the LPUSH command.")
        };
    }

    public static async Task<Result<int>> RightPushAsync(this IRedisDatabase @this, RedisKey key, string[] element, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreateRightPushCommand(key, element);

        var rawResult = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the RPUSH command.")
        };

        return result;
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
    
    public static async Task<Result<BlockingPopResult>> BlockingRightPop(this IRedisDatabase @this, TimeSpan timeout, RedisKey[] keys, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("BRPOP", null, keys, timeout, keys);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ConvertToBlockingPopResult),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message),
            _ => Result<BlockingPopResult>.Error("An unexpected error occurred while attempting to parse the result of the BLPOP command.")
        };
    }    
}