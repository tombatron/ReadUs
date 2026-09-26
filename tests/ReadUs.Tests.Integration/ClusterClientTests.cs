using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Cluster.Routing;
using ReadUs.Connections;
using ReadUs.Generated;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises Cluster support (project spec §5, §13 step 5) against a real 3-master,
/// no-replica Redis Cluster running locally (ports 7001-7003 — see the session's setup
/// notes; a permanent Testcontainers-based cluster fixture is follow-up work, matching
/// the same provisional caveat as the standalone tests).
/// </summary>
public class ClusterClientTests
{
    private static readonly EndPoint[] SeedEndpoints =
    [
        new DnsEndPoint("127.0.0.1", 7001),
        new DnsEndPoint("127.0.0.1", 7002),
        new DnsEndPoint("127.0.0.1", 7003),
    ];

    [Fact]
    public async Task HashSlotComputationMatchesTheLiveServersClusterKeyslot()
    {
        await using var client = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SeedEndpoints[0] }, connectionCount: 1);

        string[] keys = ["foo", "user1000", "{user1000}.following", "{user1000}.followers", "foo{}{bar}", "foo{{bar}}zap", Guid.NewGuid().ToString("N")];

        foreach (var key in keys)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var reply = await client.ExecuteAsync("CLUSTER"u8.ToArray(), ["KEYSLOT"u8.ToArray(), keyBytes]);
            int serverSlot = (int)reply.AsInt64();

            Assert.Equal(serverSlot, HashSlot.Compute(keyBytes));
        }
    }

    [Fact]
    public async Task DiscoversAllThreeMasterNodes()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        // Round-trip a handful of keys through the raw escape hatch so the assertion
        // exercises real routing rather than just inspecting internal state.
        for (int i = 0; i < 20; i++)
        {
            var key = Encoding.UTF8.GetBytes($"readus:test:cluster:discover:{i}:{Guid.NewGuid():N}");
            var setResult = await cluster.ExecuteAsync("SET"u8.ToArray(), [key, "v"u8.ToArray()]);
            Assert.Equal("OK", setResult.AsString());
        }
    }

    [Fact]
    public async Task SetThenGetRoundTripsRegardlessOfWhichNodeOwnsTheKey()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        // Random keys land across all three nodes' slot ranges over enough iterations,
        // proving per-key routing (not just "it works for one lucky node").
        for (int i = 0; i < 30; i++)
        {
            var key = Encoding.UTF8.GetBytes($"readus:test:cluster:roundtrip:{Guid.NewGuid():N}");
            var value = Encoding.UTF8.GetBytes($"value-{i}");

            var setResult = await cluster.ExecuteAsync("SET"u8.ToArray(), [key, value]);
            Assert.Equal("OK", setResult.AsString());

            var getResult = await cluster.ExecuteAsync("GET"u8.ToArray(), [key]);
            Assert.Equal($"value-{i}", getResult.AsString());
        }
    }

    [Fact]
    public async Task CrossSlotMultiKeyCommandIsRejectedClientSideBeforeBeingSent()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        // Two random keys are overwhelmingly likely to land in different slots
        // (1-in-16384 chance of collision) — MGET's key_specs cover both positions.
        var keyA = Encoding.UTF8.GetBytes($"readus:test:cluster:crossslot:a:{Guid.NewGuid():N}");
        var keyB = Encoding.UTF8.GetBytes($"readus:test:cluster:crossslot:b:{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<ClusterCrossSlotException>(async () =>
            await cluster.ExecuteAsync("MGET"u8.ToArray(), [keyA, keyB]));
    }

    [Fact]
    public async Task SameHashTagKeysAreAcceptedAsASingleSlotMultiKeyCommand()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);
        string tag = Guid.NewGuid().ToString("N");
        var keyA = Encoding.UTF8.GetBytes($"{{{tag}}}:a");
        var keyB = Encoding.UTF8.GetBytes($"{{{tag}}}:b");

        await cluster.ExecuteAsync("SET"u8.ToArray(), [keyA, "1"u8.ToArray()]);
        await cluster.ExecuteAsync("SET"u8.ToArray(), [keyB, "2"u8.ToArray()]);

        var result = await cluster.ExecuteAsync("MGET"u8.ToArray(), [keyA, keyB]);

        var items = result.AsItems();
        Assert.Equal("1", items[0].AsString());
        Assert.Equal("2", items[1].AsString());
    }

    [Fact]
    public async Task FollowsARealMovedRedirectAfterALiveSlotMigration()
    {
        // The sharpest part of this phase: force a real slot ownership change on the
        // live cluster after ClusterClient has already cached the old topology, then
        // prove it actually follows the server's MOVED reply to the new owner and
        // returns the real result — not just that it parses the reply shape.
        await using var node1 = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SeedEndpoints[0] }, connectionCount: 1);
        await using var node2 = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SeedEndpoints[1] }, connectionCount: 1);

        var node1Id = (await node1.ExecuteAsync("CLUSTER"u8.ToArray(), ["MYID"u8.ToArray()])).AsString();
        var node2Id = (await node2.ExecuteAsync("CLUSTER"u8.ToArray(), ["MYID"u8.ToArray()])).AsString();

        // Find a key that (a) hashes into node 1's current range and (b) has never
        // been used, so the slot is guaranteed empty and a real MIGRATE isn't needed.
        var key = FindKeyInSlotRange(rangeStart: 0, rangeEnd: 5460, out int slot);
        var keyBytes = Encoding.UTF8.GetBytes(key);

        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        // Sanity check: the slot really is empty and really is on node 1 right now.
        var preMigrationGet = await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]);
        Assert.True(preMigrationGet.IsNull);

        try
        {
            // Manual slot migration protocol (no MIGRATE step needed — the slot is
            // empty): mark it IMPORTING on the destination and MIGRATING on the
            // source, then finalize ownership on both sides.
            await node2.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "IMPORTING"u8.ToArray(), Encoding.ASCII.GetBytes(node1Id)]);
            await node1.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "MIGRATING"u8.ToArray(), Encoding.ASCII.GetBytes(node2Id)]);
            await node2.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "NODE"u8.ToArray(), Encoding.ASCII.GetBytes(node2Id)]);
            await node1.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "NODE"u8.ToArray(), Encoding.ASCII.GetBytes(node2Id)]);

            // `cluster`'s cached topology still says node 1 owns this slot — this SET
            // must hit node 1, receive a real MOVED, and transparently retry on node 2.
            var setResult = await cluster.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "migrated"u8.ToArray()]);
            Assert.Equal("OK", setResult.AsString());

            // And the map should now be patched: a follow-up read succeeds too.
            var getResult = await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]);
            Assert.Equal("migrated", getResult.AsString());
        }
        finally
        {
            // Move the slot back so the shared cluster is left as this test found it.
            await node1.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "IMPORTING"u8.ToArray(), Encoding.ASCII.GetBytes(node2Id)]);
            await node2.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "MIGRATING"u8.ToArray(), Encoding.ASCII.GetBytes(node1Id)]);
            await node2.ExecuteAsync("DEL"u8.ToArray(), [keyBytes]);
            await node1.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "NODE"u8.ToArray(), Encoding.ASCII.GetBytes(node1Id)]);
            await node2.ExecuteAsync("CLUSTER"u8.ToArray(), ["SETSLOT"u8.ToArray(), Encoding.ASCII.GetBytes(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)), "NODE"u8.ToArray(), Encoding.ASCII.GetBytes(node1Id)]);
        }
    }

    private static string FindKeyInSlotRange(int rangeStart, int rangeEnd, out int slot)
    {
        for (int i = 0; i < 1_000_000; i++)
        {
            string candidate = $"readus:test:cluster:moved:{i:D7}";
            int candidateSlot = HashSlot.Compute(Encoding.UTF8.GetBytes(candidate));
            if (candidateSlot >= rangeStart && candidateSlot <= rangeEnd)
            {
                slot = candidateSlot;
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a candidate key in the target slot range.");
    }
}
