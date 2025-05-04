using System.Runtime.CompilerServices;
using ReadUs.Parser;
using Tombatron.Results;

namespace ReadUs.Commands;

public static partial class Commands
{
    private static Result<string> ConvertToResultString(ParseResult result) =>
        Result<string>.Ok(result.ToString());

    private static Result<BlockingPopResult> ConvertToBlockingPopResult(ParseResult result) =>
        Result<BlockingPopResult>.Ok(result);
    
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
    
    private static Result<int> ParseAndReturnInt(ParseResult result)
    {
        if (result.Type == ResultType.Integer)
        {
            return Result<int>.Ok(int.Parse(result.ToString()));
        }

        return Result<int>.Error($"We expected an integer type in the reply but got {result.Type.ToString()} instead.");
    }
}