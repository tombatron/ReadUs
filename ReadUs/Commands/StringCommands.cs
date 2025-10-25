using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static partial class Commands
{
    public static async Task<Result<string>> Get(this IRedisDatabase @this, RedisKey key, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("GET", null, [key], null, key);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ConvertToResultString),
            Error<ParseResult> err => Result<string>.Error(err.Message)
        };
    }

    public static async Task<Result> Set(this IRedisDatabase @this, RedisKey key, string value, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("SET", null, [key], null, key, value);

        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message)
        };
    }
    
    public static async Task<Result> SetMultiple(this IRedisDatabase @this, KeyValuePair<RedisKey, string>[]? keysAndValues, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("MSET", null, keysAndValues.Keys(), null, keysAndValues!);
        
        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value),
            Error<ParseResult> err => Result.Error(err.Message)
        };
    }    
}