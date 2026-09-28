using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.PubSub;

/// <summary>
/// Pub/Sub subscriptions (project spec §10: "an <c>IAsyncEnumerable&lt;RedisMessage&gt;</c>-
/// style subscription API... covering <c>SUBSCRIBE</c>/<c>PSUBSCRIBE</c> and cluster
/// sharded pub/sub (<c>SSUBSCRIBE</c>)"). Owns one dedicated (non-pooled) connection —
/// same reasoning as <see cref="Caching.ClientSideCache"/>: subscription state is a
/// property of one physical connection, not something layerable over the Tier 1 pool.
///
/// A real protocol wrinkle drives this type's existence rather than just calling the
/// already-generated <c>ReadUs.Generated.PubsubCommands.SubscribeAsync</c> extension
/// method: since ReadUs always negotiates RESP3 (project spec §1), the server sends a
/// <c>SUBSCRIBE</c>/<c>PSUBSCRIBE</c>/<c>SSUBSCRIBE</c> confirmation (and their
/// <c>UN</c>/<c>PUN</c>/<c>SUN</c> counterparts) as an out-of-band Push frame, not as
/// the ordinary correlated reply <see cref="RedisConnection.SendAsync"/> expects — the
/// generated extension method would hang forever if actually used for subscribing (left
/// in place regardless; still a technically-correct low-level escape hatch for a caller
/// who wants to hand-roll their own push handling). This type sends subscription-state
/// commands via <see cref="RedisConnection.SendSubscriptionCommandAsync"/> instead, and
/// correlates their confirmations — and every subsequent <c>message</c>/<c>pmessage</c>/
/// <c>smessage</c> push — itself, via <see cref="RedisConnection.OnPush"/>.
///
/// Each <c>SubscribeAsync</c>/<c>PSubscribeAsync</c>/<c>SSubscribeAsync</c> call is an
/// async iterator: it sends the subscribe command, awaits the matching confirmation
/// push, then yields messages from an unbounded <see cref="Channel{T}"/> that the push
/// handler writes into. A <c>try</c>/<c>finally</c> around the yield loop sends the
/// matching unsubscribe command on the way out (loop break, cancellation, or disposal),
/// so unsubscribing is deterministic and automatic — the same "owns its lifetime, cleans
/// up on scope exit" shape already used by <see cref="Transactions.RedisTransaction"/>
/// and <see cref="Pooling.ConnectionLease"/>.
///
/// Deliberately out of scope, matching <see cref="Caching.ClientSideCache"/>'s identical
/// limit: no auto-reconnect after the connection faults — <see cref="RedisConnection.OnFaulted"/>
/// completes every open subscription with an exception instead, so a caller's
/// <c>await foreach</c> fails clearly rather than hanging forever, but nothing here
/// re-subscribes automatically. Also out of scope: subscribing to several
/// channels/patterns in a single call (Redis's own multi-argument <c>SUBSCRIBE</c> form)
/// — a caller wanting several just calls this multiple times, each independently
/// confirmed.
/// </summary>
public sealed class RedisSubscriber : IAsyncDisposable
{
    private static readonly byte[] SubscribeCommand = "SUBSCRIBE"u8.ToArray();
    private static readonly byte[] UnsubscribeCommand = "UNSUBSCRIBE"u8.ToArray();
    private static readonly byte[] PsubscribeCommand = "PSUBSCRIBE"u8.ToArray();
    private static readonly byte[] PunsubscribeCommand = "PUNSUBSCRIBE"u8.ToArray();
    private static readonly byte[] SsubscribeCommand = "SSUBSCRIBE"u8.ToArray();
    private static readonly byte[] SunsubscribeCommand = "SUNSUBSCRIBE"u8.ToArray();

    private readonly RedisConnection _connection;

    private readonly ConcurrentDictionary<string, Channel<RedisPubSubMessage>> _channelSubscriptions = new();
    private readonly ConcurrentDictionary<string, Channel<RedisPubSubMessage>> _patternSubscriptions = new();
    private readonly ConcurrentDictionary<string, Channel<RedisPubSubMessage>> _shardSubscriptions = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pendingChannelConfirmations = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pendingPatternConfirmations = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pendingShardConfirmations = new();

    private RedisSubscriber(RedisConnection connection) => _connection = connection;

    public static async Task<RedisSubscriber> ConnectAsync(RedisConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var connection = await RedisConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        var subscriber = new RedisSubscriber(connection);
        connection.OnPush = subscriber.HandlePush;
        connection.OnFaulted = subscriber.HandleFaulted;
        return subscriber;
    }

    public IAsyncEnumerable<RedisPubSubMessage> SubscribeAsync(string channel, CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync(channel, SubscribeCommand, UnsubscribeCommand, _channelSubscriptions, _pendingChannelConfirmations, cancellationToken);

    public IAsyncEnumerable<RedisPubSubMessage> PSubscribeAsync(string pattern, CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync(pattern, PsubscribeCommand, PunsubscribeCommand, _patternSubscriptions, _pendingPatternConfirmations, cancellationToken);

    /// <summary>Cluster sharded pub/sub (project spec §5/§10). Correct against a standalone server too — Redis treats it as an ordinary channel there — but only actually shard-routed when opened via <c>ClusterClient.CreateShardSubscriberAsync</c> (<c>ReadUs.Cluster</c>), which resolves the owning node first.</summary>
    public IAsyncEnumerable<RedisPubSubMessage> SSubscribeAsync(string shardChannel, CancellationToken cancellationToken = default) =>
        SubscribeCoreAsync(shardChannel, SsubscribeCommand, SunsubscribeCommand, _shardSubscriptions, _pendingShardConfirmations, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        CompleteAllChannelsCleanly(_channelSubscriptions);
        CompleteAllChannelsCleanly(_patternSubscriptions);
        CompleteAllChannelsCleanly(_shardSubscriptions);

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private async IAsyncEnumerable<RedisPubSubMessage> SubscribeCoreAsync(
        string name,
        byte[] subscribeCommand,
        byte[] unsubscribeCommand,
        ConcurrentDictionary<string, Channel<RedisPubSubMessage>> subscriptions,
        ConcurrentDictionary<string, TaskCompletionSource> pendingConfirmations,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<RedisPubSubMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        if (!subscriptions.TryAdd(name, channel))
        {
            throw new InvalidOperationException($"Already subscribed to '{name}' on this subscriber.");
        }

        var subscribed = false;
        try
        {
            await SendAndAwaitConfirmationAsync(name, subscribeCommand, pendingConfirmations, cancellationToken).ConfigureAwait(false);
            subscribed = true;

            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            subscriptions.TryRemove(name, out _);

            if (subscribed)
            {
                try
                {
                    // CancellationToken.None deliberately: the caller's own token may
                    // already be cancelled (that's often *why* we're unwinding), but the
                    // unsubscribe itself should still be attempted — best-effort, not
                    // itself cancellable by the same token that triggered it.
                    await SendAndAwaitConfirmationAsync(name, unsubscribeCommand, pendingConfirmations, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The connection may already be faulted or disposed; nothing left to
                    // unsubscribe from in that case.
                }
            }
        }
    }

    private async ValueTask SendAndAwaitConfirmationAsync(string name, byte[] command, ConcurrentDictionary<string, TaskCompletionSource> pendingConfirmations, CancellationToken cancellationToken)
    {
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingConfirmations[name] = confirmation;

        try
        {
            using var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), confirmation)
                : default;

            await _connection.SendSubscriptionCommandAsync(command, [Encoding.UTF8.GetBytes(name)], cancellationToken).ConfigureAwait(false);
            await confirmation.Task.ConfigureAwait(false);
        }
        finally
        {
            pendingConfirmations.TryRemove(name, out _);
        }
    }

    private void HandlePush(RedisResult push)
    {
        var fields = push.AsItems();
        if (fields.Length == 0)
        {
            return;
        }

        switch (fields[0].AsString())
        {
            case "subscribe":
            case "unsubscribe":
                CompleteConfirmation(_pendingChannelConfirmations, fields);
                break;
            case "psubscribe":
            case "punsubscribe":
                CompleteConfirmation(_pendingPatternConfirmations, fields);
                break;
            case "ssubscribe":
            case "sunsubscribe":
                CompleteConfirmation(_pendingShardConfirmations, fields);
                break;
            case "message":
                DeliverMessage(_channelSubscriptions, RedisPubSubMessageKind.Message, channel: fields[1].AsString(), pattern: null, payload: fields[2]);
                break;
            case "pmessage":
                DeliverMessage(_patternSubscriptions, RedisPubSubMessageKind.PMessage, channel: fields[2].AsString(), pattern: fields[1].AsString(), payload: fields[3]);
                break;
            case "smessage":
                DeliverMessage(_shardSubscriptions, RedisPubSubMessageKind.SMessage, channel: fields[1].AsString(), pattern: null, payload: fields[2]);
                break;
        }
    }

    private static void CompleteConfirmation(ConcurrentDictionary<string, TaskCompletionSource> pendingConfirmations, ReadOnlySpan<RedisResult> fields)
    {
        if (fields.Length < 2 || fields[1].IsNull)
        {
            // UNSUBSCRIBE with no active subscriptions reports a null channel — nothing
            // was pending for that anyway.
            return;
        }

        if (pendingConfirmations.TryRemove(fields[1].AsString(), out var confirmation))
        {
            confirmation.TrySetResult();
        }
    }

    private static void DeliverMessage(ConcurrentDictionary<string, Channel<RedisPubSubMessage>> subscriptions, RedisPubSubMessageKind kind, string channel, string? pattern, RedisResult payload)
    {
        // A pmessage's subscription is keyed by the pattern that matched, not the
        // concrete channel the publish happened on.
        var key = kind == RedisPubSubMessageKind.PMessage ? pattern! : channel;
        if (subscriptions.TryGetValue(key, out var target))
        {
            target.Writer.TryWrite(new RedisPubSubMessage(kind, channel, pattern, payload));
        }
    }

    private void HandleFaulted(Exception cause)
    {
        var exception = new RedisConnectionException("The pub/sub connection failed.", cause);

        CompleteAllConfirmationsWithException(_pendingChannelConfirmations, exception);
        CompleteAllConfirmationsWithException(_pendingPatternConfirmations, exception);
        CompleteAllConfirmationsWithException(_pendingShardConfirmations, exception);

        CompleteAllChannelsWithException(_channelSubscriptions, exception);
        CompleteAllChannelsWithException(_patternSubscriptions, exception);
        CompleteAllChannelsWithException(_shardSubscriptions, exception);
    }

    private static void CompleteAllConfirmationsWithException(ConcurrentDictionary<string, TaskCompletionSource> pendingConfirmations, Exception exception)
    {
        foreach (var name in pendingConfirmations.Keys)
        {
            if (pendingConfirmations.TryRemove(name, out var confirmation))
            {
                confirmation.TrySetException(exception);
            }
        }
    }

    private static void CompleteAllChannelsWithException(ConcurrentDictionary<string, Channel<RedisPubSubMessage>> subscriptions, Exception exception)
    {
        foreach (var name in subscriptions.Keys)
        {
            if (subscriptions.TryRemove(name, out var channel))
            {
                channel.Writer.TryComplete(exception);
            }
        }
    }

    private static void CompleteAllChannelsCleanly(ConcurrentDictionary<string, Channel<RedisPubSubMessage>> subscriptions)
    {
        foreach (var name in subscriptions.Keys)
        {
            if (subscriptions.TryRemove(name, out var channel))
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
