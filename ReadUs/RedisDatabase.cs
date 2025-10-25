using ReadUs.Commands;

namespace ReadUs;

public class RedisDatabase(RedisConnectionPool pool, int databaseId = 0) : IRedisDatabase
{
    public async Task<Result<byte[]>> Execute(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        var connection = await pool.GetConnection();

        try
        {
            var selectResult = await connection.Select(databaseId, cancellationToken).ConfigureAwait(false);

            selectResult.VerifyOk();

            return await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pool.ReturnConnection(connection);
        }
    }

    /// <summary>
    /// Subscribe to a Redis Pub/Sub channel.
    /// </summary>
    /// <param name="channel"></param>
    /// <param name="messageHandler">`T` is the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<RedisSubscription> Subscribe(string channel, Action<string> messageHandler, CancellationToken cancellationToken = default) =>
        await Subscribe([channel], (c, m) => messageHandler(m), cancellationToken);

    /// <summary>
    /// Subscribe to one or more Redis Pub/Sub channels.
    /// </summary>
    /// <param name="channels"></param>
    /// <param name="messageHandler">`T1` will be the channel the message came in from, `T2` will be the content of the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<RedisSubscription> Subscribe(string[] channels, Action<string, string> messageHandler, CancellationToken cancellationToken = default) =>
        await RedisSubscription.Initialize(pool, channels, messageHandler, cancellationToken);

    /// <summary>
    /// Subscribe to a series of Redis Pub/Sub channels using a pattern instead of a specific channel name.
    /// </summary>
    /// <param name="channelPattern"></param>
    /// <param name="messageHandler">`T1` will be the channel pattern the message came in from, `T2` will be the specific channel, and `T3` will be the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<RedisSubscription> SubscribeWithPattern(string channelPattern, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default) =>
        await SubscribeWithPattern([channelPattern], messageHandler, cancellationToken);

    /// <summary>
    /// Subscribe to a series of patterns of Redis Pub/Sub channels using multiple patterns instead of specific channel names.
    /// </summary>
    /// <param name="channelPatterns"></param>
    /// <param name="messageHandler">`T1` will be the channel pattern the message came in from, `T2` will be the specific channel, and `T3` will be the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<RedisSubscription> SubscribeWithPattern(string[] channelPatterns, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default) =>
        await RedisSubscription.InitializeWithPattern(pool, channelPatterns, messageHandler, cancellationToken);
}