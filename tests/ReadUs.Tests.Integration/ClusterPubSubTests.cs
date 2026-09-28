using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Connections;
using ReadUs.PubSub;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises <see cref="ClusterClient.CreateShardSubscriberAsync"/> (project spec
/// §5/§10) against a real, disposable Testcontainers-managed Cluster (project spec
/// §9.2). Unlike ordinary pub/sub (<see cref="PubSubTests"/>), shard pub/sub delivery
/// is shard-local — this proves the subscriber actually lands on the node owning the
/// shard channel's slot, not just that <c>SSUBSCRIBE</c> works against a single node in
/// isolation.
/// </summary>
[Collection(ClusterRedisCollection.Name)]
public class ClusterPubSubTests(ClusterRedisFixture fixture)
{
    private IReadOnlyList<EndPoint> SeedEndpoints => fixture.SeedEndpoints;

    [Fact]
    public async Task ShardSubscriberReceivesAShardPublishedMessage()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        var shardChannel = $"readus:test:cluster:pubsub:{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var subscriber = await cluster.CreateShardSubscriberAsync(shardChannel, cts.Token);

        var received = new TaskCompletionSource<RedisPubSubMessage>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var message in subscriber.SSubscribeAsync(shardChannel, cts.Token))
            {
                received.SetResult(message);
                break;
            }
        }, cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);

        var spublishArgs = new ReadOnlyMemory<byte>[] { Encoding.UTF8.GetBytes(shardChannel), "hello"u8.ToArray() };
        await cluster.ExecuteAsync("SPUBLISH"u8.ToArray(), spublishArgs, cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(RedisPubSubMessageKind.SMessage, result.Kind);
        Assert.Equal(shardChannel, result.Channel);
        Assert.Equal("hello", result.Payload.AsString());

        await consumeTask;
    }
}
