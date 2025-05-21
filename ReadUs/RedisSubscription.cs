using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public class RedisSubscription(RedisConnectionPool pool, Action<string, string, string> messageHandler) : IDisposable
{
    private IRedisConnection? _connection;
    private Task _subscriptionTask = Task.CompletedTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public RedisSubscription(RedisConnectionPool pool, Action<string, string> messageHandler) :
        this(pool, (_, channel, message) => messageHandler(channel, message))
    {
    }

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
                    throw new Exception("Whatever");
                }
            }, cancelToken),
            cancelToken);
    }

    public async Task<Result> Unsubscribe(params string[] channels)
    {
        var command = RedisCommandEnvelope.CreateUnsubscribeCommand(channels);

        var response = await _connection!.SendCommandAsync(command).ConfigureAwait(false);

        var result = response switch
        {
            Ok<byte[]> _ => Result.Ok,
            Error<byte[]> err => Result.Error(err.Message)
        };

        return result;
    }

    public async Task<Result> UnsubscribeWithPattern(params string[] channelPatterns)
    {
        var command = RedisCommandEnvelope.CreatePatternUnsubscribeCommand(channelPatterns);

        var response = await _connection!.SendCommandAsync(command).ConfigureAwait(false);

        var result = response switch
        {
            Ok<byte[]> _ => Result.Ok,
            Error<byte[]> err => Result.Error(err.Message)
        };

        return result;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        pool.ReturnConnection(_connection!);
    }
}