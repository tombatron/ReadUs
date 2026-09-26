using System.Net;
using System.Text;
using ReadUs.Connections;
using ReadUs.Protocol;
using ReadUs.Sentinel;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises Sentinel support (project spec §6, §13 step 5) against a real
/// constellation running locally: a master (port 8001), a replica (port 8002), and
/// three sentinels (ports 8100-8102) monitoring service name "mymaster" — see the
/// session's setup notes. Provisional, same caveat as the Cluster tests: a permanent
/// Testcontainers-based fixture is follow-up work (project spec §9.2).
/// </summary>
public class SentinelClientTests
{
    private static readonly IReadOnlyList<EndPoint> SentinelEndpoints =
    [
        new DnsEndPoint("127.0.0.1", 8100),
        new DnsEndPoint("127.0.0.1", 8101),
        new DnsEndPoint("127.0.0.1", 8102),
    ];

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
        for (int i = 0; i < 60; i++)
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
}
