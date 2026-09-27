using System.Text;
using ReadUs.Connections;
using ReadUs.Protocol;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Project spec §9.2's fault-injection ask, the two scenarios not already covered by a
/// real-infrastructure test elsewhere in this project: a mid-response TCP reset (an
/// abrupt connection death while a reply is in flight) and partial/sliced delivery (a
/// reply arriving in many tiny fragments rather than one clean read). Simulated
/// MOVED/ASK/CLUSTERDOWN sequences, a forced Sentinel failover, and blocking-command
/// cancellation racing the server's reply are already exercised elsewhere
/// (<see cref="ClusterClientTests"/>, <see cref="SentinelClientTests"/>, the exhaustive
/// completion-claim state-space walker) against real live topology changes rather than
/// an injected fault, so aren't duplicated here.
/// </summary>
[Collection(ToxiproxyCollection.Name)]
public class FaultInjectionTests(ToxiproxyFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.ProxiedEndPoint,
    };

    [Fact]
    public async Task AbruptConnectionResetFailsThePendingRequestClearlyAndTheConnectionLaterRecovers()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        var before = await client.ExecuteAsync("PING"u8.ToArray(), []);
        Assert.Equal("PONG", before.AsString());

        // "downstream" = the reply flowing back from the server toward this client —
        // the connection dies exactly while a response is in flight, not before the
        // request was ever sent.
        await fixture.AddToxicAsync("reset", "reset_peer", "downstream", new { timeout = 0 });
        try
        {
            await Assert.ThrowsAsync<RedisConnectionException>(async () =>
                await client.ExecuteAsync("PING"u8.ToArray(), []));
        }
        finally
        {
            await fixture.RemoveToxicAsync("reset");
        }

        // Tier 1's self-healing (design doc §1, project spec §7) should reconnect once
        // the toxic is gone and the connection is genuinely usable again.
        RedisResult? after = null;
        for (var i = 0; i < 50 && after is null; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            try
            {
                var candidate = await client.ExecuteAsync("PING"u8.ToArray(), []);
                if (!candidate.IsError)
                {
                    after = candidate;
                }
            }
            catch
            {
                // Still mid-reconnect. Keep polling.
            }
        }

        Assert.NotNull(after);
        Assert.Equal("PONG", after!.Value.AsString());
    }

    [Fact]
    public async Task ARequestSplitIntoOneByteNetworkFragmentsStillParsesCorrectly()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        // 1-byte slices with a 1ms delay between each — a real reply of any meaningful
        // size arrives across dozens of separate TCP reads rather than one, forcing the
        // zero-copy RESP reader (design doc, project spec §8) to actually reassemble a
        // frame split across many ReadOnlySequence segments over a live socket, not
        // just an in-memory unit test's synthetic split.
        await fixture.AddToxicAsync("slicer", "slicer", "downstream", new { average_size = 1, size_variation = 0, delay = 1000 });
        try
        {
            var key = Encoding.UTF8.GetBytes($"readus:test:fault:{Guid.NewGuid():N}");
            var value = Encoding.UTF8.GetBytes(new string('x', 500));

            var setResult = await client.ExecuteAsync("SET"u8.ToArray(), [key, value]);
            Assert.Equal("OK", setResult.AsString());

            var getResult = await client.ExecuteAsync("GET"u8.ToArray(), [key]);
            Assert.Equal(new string('x', 500), getResult.AsString());
        }
        finally
        {
            await fixture.RemoveToxicAsync("slicer");
        }
    }
}
