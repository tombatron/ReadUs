using System.Diagnostics;
using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Cluster.Routing;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Protocol;

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
            var serverSlot = (int)reply.AsInt64();

            Assert.Equal(serverSlot, HashSlot.Compute(keyBytes));
        }
    }

    [Fact]
    public async Task DiscoversAllThreeMasterNodes()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        // Round-trip a handful of keys through the raw escape hatch so the assertion
        // exercises real routing rather than just inspecting internal state.
        for (var i = 0; i < 20; i++)
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
        for (var i = 0; i < 30; i++)
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
        var tag = Guid.NewGuid().ToString("N");
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
        var key = FindKeyInSlotRange(rangeStart: 0, rangeEnd: 5460, out var slot);
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

    [Fact]
    public async Task QuarantinesARepeatedlyUnreachableNodeAndReintegratesItOnceItRecovers()
    {
        // Node 3 (port 7003) owns slots 10923-16383 on this fixed 3-master cluster (see
        // `CLUSTER NODES` in the session's setup notes). There's no single Redis admin
        // command that both makes a cluster node fully unreachable *and* brings it back
        // (unlike SentinelClientTests' `SENTINEL FAILOVER`), so this is the one test in
        // this file that reaches for the container directly.
        const string containerName = "readus-cluster-7003";
        var key = FindKeyInSlotRange(rangeStart: 10923, rangeEnd: 16383, out _);
        var keyBytes = Encoding.UTF8.GetBytes(key);

        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        await RunDockerCommandAsync("stop", containerName);
        try
        {
            // This client has never successfully connected to node 3, so this also
            // exercises retrying a node whose very first connection attempt failed, not
            // just an already-open one going bad (GetOrCreateNodeClientAsync's cached-
            // faulted-task eviction, design doc §3.1).
            var sawConnectionFailure = false;
            ClusterNodeQuarantinedException? quarantined = null;
            for (var i = 0; i < 10 && quarantined is null; i++)
            {
                try
                {
                    await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]);
                }
                catch (RedisConnectionException)
                {
                    sawConnectionFailure = true;
                }
                catch (ClusterNodeQuarantinedException ex)
                {
                    quarantined = ex;
                }
            }

            Assert.True(sawConnectionFailure, "Expected at least one raw connection failure before quarantine kicked in.");
            Assert.NotNull(quarantined);

            // Quarantined: every further attempt fails fast with the same exception
            // type rather than paying for another doomed connection attempt.
            await Assert.ThrowsAsync<ClusterNodeQuarantinedException>(async () =>
                await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]));
        }
        finally
        {
            await RunDockerCommandAsync("start", containerName);
        }

        // The background prober reintegrates the node once its own PING succeeds again
        // — poll rather than assume a fixed recovery time (container restart and
        // rejoining the cluster both take a real, variable amount of time).
        RedisResult? result = null;
        for (var i = 0; i < 60 && result is null; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            try
            {
                var candidate = await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]);
                if (!candidate.IsError)
                {
                    result = candidate;
                }
            }
            catch
            {
                // Still quarantined, or the node hasn't finished rejoining the cluster
                // yet. Keep polling.
            }
        }

        Assert.NotNull(result);
        Assert.True(result!.Value.IsNull, "Never actually written while the node was down, so a successful GET should just be a miss.");
    }

    private static async Task RunDockerCommandAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the docker process.");
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} failed: {stderr}");
        }
    }

    private static string FindKeyInSlotRange(int rangeStart, int rangeEnd, out int slot)
    {
        for (var i = 0; i < 1_000_000; i++)
        {
            var candidate = $"readus:test:cluster:moved:{i:D7}";
            var candidateSlot = HashSlot.Compute(Encoding.UTF8.GetBytes(candidate));
            if (candidateSlot >= rangeStart && candidateSlot <= rangeEnd)
            {
                slot = candidateSlot;
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a candidate key in the target slot range.");
    }
}
