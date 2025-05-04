using System.Runtime.CompilerServices;
using ReadUs.Parser;
using Tombatron.Results;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static class Commands
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

    private static Result<string> ConvertToResultString(ParseResult result) =>
        Result<string>.Ok(result.ToString());
    
    private static Result<T> EvaluateResult<T>(ParseResult result, Func<ParseResult, Result<T>> converter, [CallerMemberName] string callingMember = "") where T : notnull
    {
        var evalResult = EvaluateResult(result, callingMember);
        
        return evalResult switch
        {
            Ok => converter(result),
            Error err => Result<T>.Error(err.Message),
            _ => Result<T>.Error("Ran into an unexpected (and I'll be honest, I thought it was impossible) error while evaluating the result of a Redis command.")
        };
    }
    
    private static Result EvaluateResult(ParseResult result, [CallerMemberName] string callingMember = "")
    {
        if (result.Type == ResultType.Error)
        {
            var errorMessage = $"Redis returned an error while we were invoking `{callingMember}`. The error was: {result.ToString()}";

            return Result.Error(errorMessage);
        }

        return Result.Ok;
    }
}