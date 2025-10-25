using ReadUs.Parser;
using ReadUs.Commands.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static partial class Commands
{
    public static async Task<Result<int>> ListLength(this IRedisDatabase @this, RedisKey key, CancellationToken cancellationToken = default)
    {
        var command = CreateListLengthCommand(key);

        var rawResult = await @this.Execute(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message)
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
            Error<ParseResult> err => Result<int>.Error(err.Message)
        };
    }

    public static async Task<Result<int>> RightPushAsync(this IRedisDatabase @this, RedisKey key, string[] element, CancellationToken cancellationToken = default)
    {
        var command = CreateRightPushCommand(key, element);

        var rawResult = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message)
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
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message)
        };
    }
    
    public static async Task<Result<BlockingPopResult>> BlockingRightPop(this IRedisDatabase @this, TimeSpan timeout, RedisKey[] keys, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("BRPOP", null, keys, timeout, keys);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ConvertToBlockingPopResult),
            Error<ParseResult> err => Result<BlockingPopResult>.Error(err.Message)
        };
    }    
}