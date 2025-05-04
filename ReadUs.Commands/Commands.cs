using ReadUs.Parser;
using Tombatron.Results;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static class Commands
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static async Task<Result> Select(this IRedisDatabase @this, int databaseId, CancellationToken cancellationToken = default)
    {
        RedisCommandEnvelope command = new("SELECT", null, null, TimeSpan.FromSeconds(5), databaseId);
        
        var result = await @this.Execute(command, cancellationToken);
        
        return Parse(result) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SELECT command.")
        };
    }
}