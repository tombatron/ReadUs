using ReadUs.Commands;
using ReadUs.Exceptions;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public class RedisSubscription(RedisConnectionPool pool, Action<string, string, string> messageHandler) : IDisposable
{
    private IRedisConnection? _connection;
    private Task _subscriptionTask = Task.CompletedTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private RedisSubscription(RedisConnectionPool pool, Action<string, string> messageHandler) :
        this(pool, (_, channel, message) => messageHandler(channel, message))
    {
    }

    private async Task Initialize(RedisCommandEnvelope command, CancellationToken cancellationToken)
    {
        var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token).Token;

        _connection = await pool.GetConnection().ConfigureAwait(false);
        
        _subscriptionTask = Task.Run(() => _connection.SendCommandWithMultipleResponses(command, bytes =>
            {
                var message = Parse(bytes);

                if (message is Ok<ParseResult> ok)
                {
                    if (ok.Value.TryToArray(out var values))
                    {
                        var messageType = values[0].ToString();

                        if (messageType == "message")
                        {
                            var channelValue = values[1].ToString();
                            var messageValue = values[2].ToString();

                            messageHandler(string.Empty, channelValue, messageValue);
                        }

                        if (messageType == "pmessage")
                        {
                            var patternValue = values[1].ToString();
                            var channelValue = values[2].ToString();
                            var messageValue = values[3].ToString();

                            messageHandler(patternValue, channelValue, messageValue);
                        }
                    }
                }

                if (message is Error<ParseResult> err)
                {
                    throw new RedisSubscriptionException($"There was an issue receiving a Pub/Sub message: {err.ToErrorString()}");
                }
            }, cancelToken),
            cancelToken);
    }

    public async Task Unsubscribe(string channel, CancellationToken cancellationToken = default) =>
        await Unsubscribe([channel], cancellationToken).ConfigureAwait(false);

    public async Task Unsubscribe(string[] channels, CancellationToken cancellationToken = default) =>
        await _connection!.Unsubscribe(channels, cancellationToken).ConfigureAwait(false);
    
    public async Task UnsubscribeWithPattern(string pattern, CancellationToken cancellationToken = default) =>
        await UnsubscribeWithPattern([pattern], cancellationToken).ConfigureAwait(false);
    
    public async Task UnsubscribeWithPattern(string[] patterns, CancellationToken cancellationToken = default) =>
        await _connection!.UnsubscribeWithPattern(patterns, cancellationToken).ConfigureAwait(false);
    
    internal static async Task<RedisSubscription> Initialize(RedisConnectionPool pool, string[] channels, Action<string, string> messageHandler, CancellationToken cancellationToken = default)
    {
        var subscription = new RedisSubscription(pool, messageHandler);

        await subscription.Initialize(Commands.Commands.CreateSubscribeCommand(channels), cancellationToken).ConfigureAwait(false);

        return subscription;
    }

    internal static async Task<RedisSubscription> InitializeWithPattern(RedisConnectionPool pool, string[] channelPatterns, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default)
    {
        var subscription = new RedisSubscription(pool, messageHandler);

        await subscription.Initialize(Commands.Commands.CreatePatternSubscribeCommand(channelPatterns), cancellationToken).ConfigureAwait(false);

        return subscription;
    }
    
    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        pool.ReturnConnection(_connection!);
    }
}