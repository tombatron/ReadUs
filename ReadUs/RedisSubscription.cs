using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs;

// TODO: Change this to a static factory method.
// TODO: In order to support multiple channels, we need to change the signature of the messageHandler to accept the channel name.
public class RedisSubscription(IRedisConnectionPool pool, Action<string> messageHandler) : IDisposable
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
                        var messageValue = values[2].ToString();

                        messageHandler(messageValue);
                    }
                }
            }

            if (message is Error<ParseResult> err)
            {
                throw new Exception("Whatever");
            }
        }), cancelToken);
    }

    public async Task<Result> Unsubscribe() // TODO...
    {
        // First issue the UNSUBSCRIBE command.
        var command = new RedisCommandEnvelope("UNSUBSCRIBE", null, null, null);
        
        var result = await _connection!.SendCommandAsync(command).ConfigureAwait(false);

        if (result is Ok<byte[]> _) // TODO: Looks like there's a bug in the analyzer. Remind me to fix the issue that a type check
                                    //       isn't enough to satisfy the analyzer that the "OK" case is handled.
        {
            // OK, this connection shouldn't be getting any more messages, let's go ahead and cancel that long-running task.
            await _cancellationTokenSource.CancelAsync().ConfigureAwait(false);

            return Result.Ok;
        }

        if (result is Error<byte[]> err)
        {
            // Well... something went wrong. Let's return that error.
            return Result.Error(err.Message);
        }
        
        // TODO: This is a bug in the Result analyzer. I need to adjust it such that an an `else` is acceptable to handle
        //       the inverse of an OK or ERROR case. 
        return Result.Error("An unexpected error occurred while attempting to unsubscribe.");
    }

    public void Dispose()
    {
        pool.ReturnConnection(_connection!);
    }
}