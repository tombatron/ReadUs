﻿using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs;

// TODO: Change this to a static factory method.
// TODO: In order to support multiple channels, we need to change the signature of the messageHandler to accept the channel name.
public class RedisSubscription : IDisposable
{
    private IRedisConnection? _connection;
    private IRedisConnectionPool _pool;
    private Task _subscriptionTask = Task.CompletedTask;
    private Action<string, string, string> _messageHandler;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public RedisSubscription(IRedisConnectionPool pool, Action<string, string> messageHandler) : 
        this(pool, (_, channel, message) => messageHandler(channel, message))
    {
    }

    public RedisSubscription(IRedisConnectionPool pool, Action<string, string, string> messageHandler)
    {
        _pool = pool;
        _messageHandler = messageHandler;
    }

    internal async Task Initialize(RedisCommandEnvelope command, CancellationToken cancellationToken)
    {
        var cancelToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token).Token;

        _connection = await _pool.GetConnection().ConfigureAwait(false);

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

                            _messageHandler(string.Empty, channelValue, messageValue);
                        }

                        if (messageType == "pmessage")
                        {
                            var patternValue = values[1].ToString();
                            var channelValue = values[2].ToString();
                            var messageValue = values[3].ToString();

                            _messageHandler(patternValue, channelValue, messageValue);
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

        _pool.ReturnConnection(_connection!);
    }
}