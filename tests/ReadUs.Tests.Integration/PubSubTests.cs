using System.Text;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Protocol;
using ReadUs.PubSub;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises <see cref="RedisSubscriber"/> (project spec §10) against a real, disposable
/// Testcontainers-managed server (project spec §9.2). All three subscription kinds
/// route through the same RESP3 push-confirmation machinery
/// (docs/design/state-machines.md, "Recorded during implementation of Pub/Sub") — these
/// tests exercise the plain-channel and pattern forms; <c>ClusterPubSubTests</c> covers
/// the shard-routed form, which needs a real Cluster topology to mean anything.
/// </summary>
[Collection(StandaloneRedisCollection.Name)]
public class PubSubTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task SubscribeAsyncReceivesAPublishedMessage()
    {
        await using var publisher = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        await using var subscriber = await RedisSubscriber.ConnectAsync(Options);

        var channel = $"readus:test:pubsub:{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var received = new TaskCompletionSource<RedisPubSubMessage>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var message in subscriber.SubscribeAsync(channel, cts.Token))
            {
                received.SetResult(message);
                break;
            }
        }, cts.Token);

        // No explicit "wait for the subscribe confirmation" step is needed here: the
        // enumerable's own SendAndAwaitConfirmationAsync already blocks the iterator
        // until the server confirms, so a publish issued any time after consumeTask has
        // had a moment to start is safe — the small delay below just gives the Task.Run
        // above a chance to actually reach that await.
        await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);

        await publisher.PublishAsync(Encoding.UTF8.GetBytes(channel), "hello"u8.ToArray(), cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(RedisPubSubMessageKind.Message, result.Kind);
        Assert.Equal(channel, result.Channel);
        Assert.Null(result.Pattern);
        Assert.Equal("hello", result.Payload.AsString());

        await consumeTask;
    }

    [Fact]
    public async Task PSubscribeAsyncReceivesAMessageMatchingThePattern()
    {
        await using var publisher = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        await using var subscriber = await RedisSubscriber.ConnectAsync(Options);

        var prefix = $"readus:test:pubsub:pattern:{Guid.NewGuid():N}";
        var pattern = $"{prefix}:*";
        var channel = $"{prefix}:widgets";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var received = new TaskCompletionSource<RedisPubSubMessage>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var message in subscriber.PSubscribeAsync(pattern, cts.Token))
            {
                received.SetResult(message);
                break;
            }
        }, cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);

        await publisher.PublishAsync(Encoding.UTF8.GetBytes(channel), "hello"u8.ToArray(), cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(RedisPubSubMessageKind.PMessage, result.Kind);
        Assert.Equal(channel, result.Channel);
        Assert.Equal(pattern, result.Pattern);
        Assert.Equal("hello", result.Payload.AsString());

        await consumeTask;
    }

    [Fact]
    public async Task BreakingOutOfTheLoopActuallyUnsubscribesServerSide()
    {
        await using var admin = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        await using var subscriber = await RedisSubscriber.ConnectAsync(Options);

        var channel = $"readus:test:pubsub:unsub:{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // No message is ever published — the point of this test is purely the
        // subscribe/unsubscribe lifecycle. Cancelling ends the await foreach the same
        // general way a loop break would, driving SubscribeCoreAsync's finally-block
        // UNSUBSCRIBE on the way out (sent with CancellationToken.None specifically so
        // this cancellation doesn't also cancel the cleanup it triggers).
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var _ in subscriber.SubscribeAsync(channel, cts.Token))
            {
            }
        }, cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await cts.CancelAsync();

        try
        {
            await consumeTask;
        }
        catch (OperationCanceledException)
        {
        }

        var channels = 0;
        for (var i = 0; i < 50; i++)
        {
            var reply = await admin.PubsubNumsubAsync([Encoding.UTF8.GetBytes(channel)]);
            var items = reply.AsItems();
            channels = (int)items[1].AsInt64();
            if (channels == 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(0, channels);
    }

    [Fact]
    public async Task AConnectionLevelFaultCompletesAnOpenSubscriptionWithAnException()
    {
        await using var admin = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var subscriber = await RedisSubscriber.ConnectAsync(Options);

        var channel = $"readus:test:pubsub:fault:{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Exception? observed = null;
        var consumeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in subscriber.SubscribeAsync(channel, cts.Token))
                {
                }
            }
            catch (Exception ex)
            {
                observed = ex;
            }
        }, cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);

        // A real network-level fault (project spec §9.2), not a simulation: CLIENT KILL
        // TYPE pubsub closes every connection currently in subscriber mode server-side
        // — exactly (and only) RedisSubscriber's dedicated connection, without needing
        // to know its CLIENT ID.
        await admin.ExecuteAsync("CLIENT"u8.ToArray(), ["KILL"u8.ToArray(), "TYPE"u8.ToArray(), "pubsub"u8.ToArray()], cts.Token);

        await consumeTask;

        Assert.NotNull(observed);
        Assert.IsType<RedisConnectionException>(observed);

        await subscriber.DisposeAsync();
    }
}
