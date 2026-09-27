using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Cluster.Routing;
using ReadUs.Connections;
using ReadUs.Diagnostics;
using ReadUs.Generated;
using ReadUs.Protocol;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises Cluster support (project spec §5, §13 step 5) against a real, disposable
/// Testcontainers-managed Redis Cluster (project spec §9.2): three masters, plus one
/// replica of node 1 (slots 0-5460), added specifically to exercise replica read
/// routing (design doc §3.2) against a real replica rather than only the
/// no-replica-available fallback/error paths. See <see cref="ClusterRedisFixture"/>.
/// </summary>
[Collection(ClusterRedisCollection.Name)]
public class ClusterClientTests(ClusterRedisFixture fixture)
{
    private IReadOnlyList<EndPoint> SeedEndpoints => fixture.SeedEndpoints;

    // Node 1 owns slots 0-5460 and has the one replica in this fixture; nodes 2 and 3
    // have none.
    private const int Node1RangeStart = 0;
    private const int Node1RangeEnd = 5460;

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
        // Node 3 (index 2) owns slots 10923-16383 on this 3-master cluster. There's no
        // single Redis admin command that both makes a cluster node fully unreachable
        // *and* brings it back (unlike SentinelClientTests' `SENTINEL FAILOVER`), so
        // this is the one test in this file that reaches for the container directly.
        var node3 = fixture.Containers[2];
        var key = FindKeyInSlotRange(rangeStart: 10923, rangeEnd: 16383, out _);
        var keyBytes = Encoding.UTF8.GetBytes(key);

        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        await node3.StopAsync();
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
            await node3.StartAsync();
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

    [Fact]
    public async Task PreferReplicaRoutesToTheReplicaWithoutFollowingAMovedRedirect()
    {
        await using var admin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SeedEndpoints[0] }, connectionCount: 1);
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        var key = FindRandomKeyInSlotRange(Node1RangeStart, Node1RangeEnd);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        await admin.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "from-primary"u8.ToArray()]);

        var movedRedirects = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReadUsDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "readus.cluster.redirects")
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "kind", Value: "moved" })
                    {
                        Interlocked.Add(ref movedRedirects, (int)value);
                    }
                }
            }
        });
        listener.Start();

        // Replicas answer a plain (non-READONLY) read with MOVED — if this ever comes
        // back with a moved-redirect recorded, READONLY wasn't actually issued on the
        // connection and this "succeeded" only by transparently retrying on the
        // primary, not by genuinely reading from the replica.
        var result = await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes], ReadPreference.PreferReplica);

        listener.Dispose();

        Assert.Equal("from-primary", result.AsString());
        Assert.Equal(0, movedRedirects);
    }

    [Fact]
    public async Task ReplicaOnlyThrowsWhenTheTargetShardHasNoReplica()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        // Node 2's (7002) shard has no replica in this fixture.
        var key = FindRandomKeyInSlotRange(rangeStart: 5461, rangeEnd: 10922);
        var keyBytes = Encoding.UTF8.GetBytes(key);

        await Assert.ThrowsAsync<RedisConnectionException>(async () =>
            await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes], ReadPreference.ReplicaOnly));
    }

    [Fact]
    public async Task RoundRobinReadPreferenceSendsTrafficToBothThePrimaryAndTheReplica()
    {
        await using var admin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SeedEndpoints[0] }, connectionCount: 1);
        await using var replicaAdmin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = fixture.ReplicaEndpoint }, connectionCount: 1);
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        var key = FindRandomKeyInSlotRange(Node1RangeStart, Node1RangeEnd);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        await admin.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "v"u8.ToArray()]);

        var primaryBefore = await TotalCommandsProcessedAsync(admin);
        var replicaBefore = await TotalCommandsProcessedAsync(replicaAdmin);

        for (var i = 0; i < 20; i++)
        {
            var result = await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes], ReadPreference.RoundRobin);
            Assert.Equal("v", result.AsString());
        }

        var primaryAfter = await TotalCommandsProcessedAsync(admin);
        var replicaAfter = await TotalCommandsProcessedAsync(replicaAdmin);

        Assert.True(primaryAfter > primaryBefore, "Expected round-robin to send at least some reads to the primary.");
        Assert.True(replicaAfter > replicaBefore, "Expected round-robin to send at least some reads to the replica.");
    }

    [Fact]
    public async Task NonPrimaryReadPreferenceOnAWriteCommandIsRejectedClientSide()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);
        var keyBytes = Encoding.UTF8.GetBytes($"readus:test:cluster:readpref:{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await cluster.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "v"u8.ToArray()], ReadPreference.PreferReplica));

        Assert.Equal("readPreference", ex.ParamName);
    }

    [Fact]
    public async Task ClientWideDefaultReadPreferenceAppliesToReadsButNeverToWrites()
    {
        await using var admin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = SeedEndpoints[0] }, connectionCount: 1);
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints, defaultReadPreference: ReadPreference.PreferReplica);

        var key = FindRandomKeyInSlotRange(Node1RangeStart, Node1RangeEnd);
        var keyBytes = Encoding.UTF8.GetBytes(key);
        await admin.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "from-primary"u8.ToArray()]);

        // A write via the plain overload must still succeed even though this client's
        // default read preference is non-primary — a write can never honor anything
        // but PrimaryOnly, regardless of the configured default.
        var setResult = await cluster.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "still-writable"u8.ToArray()]);
        Assert.Equal("OK", setResult.AsString());

        // A read via the plain overload (no explicit ReadPreference argument) picks up
        // the client-wide default. Verified the same way the explicit-overload
        // PreferReplica test is: zero MOVED redirects means READONLY was actually
        // issued on a replica connection, proving this genuinely went to the replica.
        var movedRedirects = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ReadUsDiagnostics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "readus.cluster.redirects")
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "kind", Value: "moved" })
                    {
                        Interlocked.Add(ref movedRedirects, (int)value);
                    }
                }
            }
        });
        listener.Start();

        var getResult = await cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]);

        listener.Dispose();

        Assert.Equal("still-writable", getResult.AsString());
        Assert.Equal(0, movedRedirects);
    }

    [Fact]
    public async Task ConcurrentFirstContactWithANewNodeOnlyEverConnectsOnce()
    {
        // Design doc §5's "Recorded during §13 step 5" list: ConcurrentDictionary.GetOrAdd
        // can invoke its factory more than once under contention, which for a bare
        // Task<RedisClient> factory meant two concurrent first-contacts of the same new
        // node could each open a real RedisClient (its own live socket pool), with the
        // loser silently leaked. Fixed via a Lazy<Task<RedisClient>> wrapper. Proven here
        // by counting how many times the options factory itself gets invoked for the
        // node under real concurrent load, right after ClusterClient.ConnectAsync — at
        // that point only a transient seed connection exists, so a burst of commands
        // all landing on the same not-yet-contacted node genuinely races first contact.
        var connectionAttempts = new ConcurrentDictionary<string, int>();
        RedisConnectionOptions CountingOptionsFactory(EndPoint endpoint)
        {
            connectionAttempts.AddOrUpdate(endpoint.ToString()!, 1, (_, count) => count + 1);
            return new RedisConnectionOptions { EndPoint = endpoint };
        }

        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints, CountingOptionsFactory);

        var tasks = new Task[30];
        for (var i = 0; i < tasks.Length; i++)
        {
            var keyBytes = Encoding.UTF8.GetBytes(FindRandomKeyInSlotRange(rangeStart: 5461, rangeEnd: 10922));
            tasks[i] = cluster.ExecuteAsync("GET"u8.ToArray(), [keyBytes]).AsTask();
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, connectionAttempts.GetValueOrDefault(SeedEndpoints[1].ToString()!));
    }

    private static async Task<long> TotalCommandsProcessedAsync(RedisClient client)
    {
        var info = (await client.ExecuteAsync("INFO"u8.ToArray(), ["stats"u8.ToArray()])).AsString();
        foreach (var line in info.Split("\r\n"))
        {
            if (line.StartsWith("total_commands_processed:", StringComparison.Ordinal))
            {
                return long.Parse(line.AsSpan("total_commands_processed:".Length), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("total_commands_processed not found in INFO stats.");
    }

    /// <summary>
    /// Same idea as <see cref="FindKeyInSlotRange"/> but randomized rather than
    /// deterministic — for tests that set their own value and don't need the "never
    /// used before" guarantee, so they don't collide with
    /// <see cref="FollowsARealMovedRedirectAfterALiveSlotMigration"/>'s specific,
    /// deterministically-derived key in the same slot range.
    /// </summary>
    private static string FindRandomKeyInSlotRange(int rangeStart, int rangeEnd)
    {
        for (var i = 0; i < 1_000_000; i++)
        {
            var candidate = $"readus:test:cluster:readpref:{Guid.NewGuid():N}";
            if (HashSlot.Compute(Encoding.UTF8.GetBytes(candidate)) is var slot && slot >= rangeStart && slot <= rangeEnd)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a candidate key in the target slot range.");
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
