using System.Net;
using ReadUs.Connections;
using ReadUs.Pooling;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises Tier 1's self-healing (project spec §7: "cross-AZ/cross-region latency
/// and transient managed-service blips are the norm") directly against
/// <see cref="MultiplexedConnectionPool"/>, since <see cref="RedisClient"/> doesn't
/// expose its underlying connections for a test to fault deliberately.
/// </summary>
public class MultiplexedConnectionPoolHealingTests
{
    private static RedisConnectionOptions Options => new()
    {
        EndPoint = new DnsEndPoint("localhost", 6379),
    };

    [Fact]
    public async Task SkipsAFaultedConnectionImmediatelyAndSelfHealsInTheBackground()
    {
        await using var pool = await MultiplexedConnectionPool.CreateAsync(Options, size: 2);

        // Simulate one connection faulting (a dropped socket, a managed-service blip)
        // by disposing it out from under the pool.
        await pool.Rent().DisposeAsync();

        // Skip-ahead: every call right after the fault must still succeed by landing on
        // the other, healthy connection — round-robin's turn landing on the faulted
        // slot must not surface as a failure to the caller.
        for (int i = 0; i < 20; i++)
        {
            var result = await pool.ExecuteAsync("PING"u8.ToArray(), []);
            Assert.Equal("PONG", result.AsString());
        }

        // Give the background health loop time to replace the faulted slot, then
        // confirm the pool is fully healthy again (both slots usable, not just the one
        // that happened to survive).
        await Task.Delay(TimeSpan.FromSeconds(3));

        for (int i = 0; i < 20; i++)
        {
            var result = await pool.ExecuteAsync("PING"u8.ToArray(), []);
            Assert.Equal("PONG", result.AsString());
        }
    }
}
