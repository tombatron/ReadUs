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
    
    internal async Task Initialize(RedisCommandEnvelope command, CancellationToken cancellationToken)
    {
        _connection = await pool.GetConnection().ConfigureAwait(false);

        // TODO: Let's not await this, but rather store the task and await it in the Dispose method. or something
        await _connection.SendCommandWithMultipleResponses(command, bytes =>
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
        }, cancellationToken);
    }
    
    public void Dispose()
    {
        pool.ReturnConnection(_connection!);
    }
}