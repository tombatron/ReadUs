using System.Net;
using System.Text;
using ReadUs.Connections;
using ReadUs.Protocol;
using ReadUs.PubSub;
using ReadUs.Sentinel;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises Sentinel support (project spec §6, §13 step 5) against a real, disposable
/// Testcontainers-managed constellation (project spec §9.2): a master, a replica, and
/// three sentinels monitoring service name "mymaster" — see <see cref="SentinelRedisFixture"/>.
/// </summary>
[Collection(SentinelRedisCollection.Name)]
public class SentinelClientTests(SentinelRedisFixture fixture)
{
    private IReadOnlyList<EndPoint> SentinelEndpoints => fixture.SentinelEndpoints;

    private const string ServiceName = "mymaster";

    [Fact]
    public async Task DiscoversTheMasterAndRoutesCommandsToIt()
    {
        await using var sentinel = await SentinelClient.ConnectAsync(SentinelEndpoints, ServiceName);

        var key = Encoding.UTF8.GetBytes($"readus:test:sentinel:{Guid.NewGuid():N}");
        var setResult = await sentinel.ExecuteAsync("SET"u8.ToArray(), [key, "hello"u8.ToArray()]);
        Assert.Equal("OK", setResult.AsString());

        var getResult = await sentinel.ExecuteAsync("GET"u8.ToArray(), [key]);
        Assert.Equal("hello", getResult.AsString());

        // Proves the client is actually talking to whichever node is currently the
        // master, rather than just any reachable node in the constellation.
        var role = await sentinel.ExecuteAsync("ROLE"u8.ToArray(), []);
        Assert.Equal("master", role.AsItems()[0].AsString());
    }

    [Fact]
    public async Task DiscoveryFailsClearlyWhenNoSentinelIsReachable()
    {
        IReadOnlyList<EndPoint> unreachable = [new DnsEndPoint("127.0.0.1", 8199)];

        await Assert.ThrowsAsync<SentinelDiscoveryException>(async () =>
            await SentinelClient.ConnectAsync(unreachable, ServiceName));
    }

    [Fact]
    public async Task RealFailoverIsDetectedAndSubsequentCommandsRouteToTheNewMaster()
    {
        // The sharpest part of this phase: force Sentinel to actually run a failover on
        // the live constellation, then prove the client follows it — via whichever of
        // the two documented signals wins (the +switch-master push, or the polling
        // safety net) — rather than staying stuck talking to the now-demoted old master.
        await using var sentinel = await SentinelClient.ConnectAsync(SentinelEndpoints, ServiceName);

        var beforeRole = await sentinel.ExecuteAsync("ROLE"u8.ToArray(), []);
        Assert.Equal("master", beforeRole.AsItems()[0].AsString());

        await using (var sentinelAdmin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SentinelEndpoints[0] }, connectionCount: 1))
        {
            await sentinelAdmin.ExecuteAsync("SENTINEL"u8.ToArray(), ["FAILOVER"u8.ToArray(), Encoding.ASCII.GetBytes(ServiceName)]);
        }

        RedisResult? role = null;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            try
            {
                var candidate = await sentinel.ExecuteAsync("ROLE"u8.ToArray(), []);
                if (candidate.AsItems()[0].AsString() == "master")
                {
                    role = candidate;
                    break;
                }
            }
            catch
            {
                // Mid-failover: the connection to whatever we're currently pointed at
                // may be actively failing right now. Keep polling.
            }
        }

        Assert.NotNull(role);
        Assert.Equal("master", role!.Value.AsItems()[0].AsString());

        var key = Encoding.UTF8.GetBytes($"readus:test:sentinel:postfailover:{Guid.NewGuid():N}");
        await sentinel.ExecuteAsync("SET"u8.ToArray(), [key, "still-works"u8.ToArray()]);
        var getResult = await sentinel.ExecuteAsync("GET"u8.ToArray(), [key]);
        Assert.Equal("still-works", getResult.AsString());
    }

    [Fact]
    public async Task CreateSubscriberAsyncTargetsTheCurrentMasterAndReceivesAPublishedMessage()
    {
        await using var sentinel = await SentinelClient.ConnectAsync(SentinelEndpoints, ServiceName);
        await using var subscriber = await sentinel.CreateSubscriberAsync();

        var channel = $"readus:test:sentinel:pubsub:{Guid.NewGuid():N}";
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

        await Task.Delay(TimeSpan.FromMilliseconds(200), cts.Token);

        await sentinel.ExecuteAsync("PUBLISH"u8.ToArray(), [Encoding.UTF8.GetBytes(channel), "hello"u8.ToArray()], cts.Token);

        var result = await received.Task.WaitAsync(cts.Token);
        Assert.Equal("hello", result.Payload.AsString());

        await consumeTask;
    }
}
