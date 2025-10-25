using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs.Commands;

public static partial class Commands
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static async Task<Result<int>> Publish(this IRedisDatabase @this, string channel, string message, CancellationToken cancellationToken = default)
    {
        var command = CreatePublishCommand(channel, message);

        var result = await @this.Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message)
        };
    }
}