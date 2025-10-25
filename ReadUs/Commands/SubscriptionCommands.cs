namespace ReadUs.Commands;

public static partial class Commands
{
    public static async Task<Result> Unsubscribe(this IRedisConnection @this, string[] channels, CancellationToken cancellationToken = default)
    {
        var response = await @this.SendCommandAsync(CreateUnsubscribeCommand(channels), cancellationToken).ConfigureAwait(false);

        return response switch
        {
            Ok<byte[]> _ => Result.Ok,
            Error<byte[]> err => Result.Error(err.Message)
        };
    }

    public static async Task<Result> UnsubscribeWithPattern(this IRedisConnection @this, string[] channelPatterns, CancellationToken cancellationToken = default)
    {
        var response = await @this.SendCommandAsync(CreatePatternUnsubscribeCommand(channelPatterns), cancellationToken).ConfigureAwait(false);

        return response switch
        {
            Ok<byte[]> _ => Result.Ok,
            Error<byte[]> err => Result.Error(err.Message)
        };
    }
}