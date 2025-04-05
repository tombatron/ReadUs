using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs;

// TODO: Change this to a static factory method.
// TODO: In order to support multiple channels, we need to change the signature of the messageHandler to accept the channel name.
public class RedisSubscription(IRedisConnectionPool pool, Action<string, string> messageHandler) : IDisposable
{
    private IRedisConnection? _connection;
    private Task _subscriptionTask = Task.CompletedTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    internal async Task Initialize(RedisCommandEnvelope command, CancellationToken cancellationToken)
    {
        var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token).Token;

        _connection = await pool.GetConnection().ConfigureAwait(false);

        // TODO: Let's not await this, but rather store the task and await it in the Dispose method. or something
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

                            messageHandler(channelValue, messageValue);
                        }
                    }
                }

                if (message is Error<ParseResult> err)
                {
                    throw new Exception("Whatever");
                }
            }, cancelToken),
            cancelToken);
    }

    public async Task<Result> Unsubscribe(params string[] channels) // TODO...
    {
        var command = new RedisCommandEnvelope("UNSUBSCRIBE", channels, null, null, false);

        var response = await _connection!.SendCommandAsync(command).ConfigureAwait(false);

        var result = response switch
        {
            Ok<byte[]> _ => Result.Ok,
            Error<byte[]> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occured while attempting to unsubscribe.")
        };

        return result;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        
        pool.ReturnConnection(_connection!);
    }
}