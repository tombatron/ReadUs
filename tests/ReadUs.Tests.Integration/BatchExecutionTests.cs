using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Cluster.Routing;
using ReadUs.Connections;

namespace ReadUs.Tests.Integration;

/// <summary>Exercises <c>ExecuteBatchAsync</c> (project spec §5/§8's pipelining goal) on both the standalone and Cluster clients against real servers.</summary>
public class BatchExecutionTests
{
    private static RedisConnectionOptions StandaloneOptions => new()
    {
        EndPoint = new DnsEndPoint("localhost", 6379),
    };

    private static readonly EndPoint[] ClusterSeedEndpoints =
    [
        new DnsEndPoint("127.0.0.1", 7001),
        new DnsEndPoint("127.0.0.1", 7002),
        new DnsEndPoint("127.0.0.1", 7003),
    ];

    [Fact]
    public async Task StandaloneBatchPreservesOrderAndReturnsAllResults()
    {
        await using var client = await RedisClient.ConnectAsync(StandaloneOptions, connectionCount: 4);

        const int count = 20;
        var keys = new byte[count][];
        var setCommands = new RedisBatchCommand[count];
        for (var i = 0; i < count; i++)
        {
            keys[i] = Encoding.UTF8.GetBytes($"readus:test:batch:{i}:{Guid.NewGuid():N}");
            setCommands[i] = new RedisBatchCommand { CommandName = "SET"u8.ToArray(), Args = [keys[i], Encoding.UTF8.GetBytes($"value-{i}")] };
        }

        var setResults = await client.ExecuteBatchAsync(setCommands);
        Assert.All(setResults, r => Assert.Equal("OK", r.AsString()));

        var getCommands = new RedisBatchCommand[count];
        for (var i = 0; i < count; i++)
        {
            getCommands[i] = new RedisBatchCommand { CommandName = "GET"u8.ToArray(), Args = [keys[i]] };
        }

        var getResults = await client.ExecuteBatchAsync(getCommands);

        for (var i = 0; i < count; i++)
        {
            Assert.Equal($"value-{i}", getResults[i].AsString());
        }
    }

    [Fact]
    public async Task StandaloneBatchCapturesACommandLevelErrorPerItemWithoutAbortingTheBatch()
    {
        await using var client = await RedisClient.ConnectAsync(StandaloneOptions, connectionCount: 1);

        var goodKey = Encoding.UTF8.GetBytes($"readus:test:batch:good:{Guid.NewGuid():N}");
        var listKey = Encoding.UTF8.GetBytes($"readus:test:batch:wrongtype:{Guid.NewGuid():N}");
        await client.ExecuteAsync("SET"u8.ToArray(), [goodKey, "hello"u8.ToArray()]);
        await client.ExecuteAsync("LPUSH"u8.ToArray(), [listKey, "item"u8.ToArray()]);

        var results = await client.ExecuteBatchAsync(
        [
            new RedisBatchCommand { CommandName = "GET"u8.ToArray(), Args = [goodKey] },
            new RedisBatchCommand { CommandName = "GET"u8.ToArray(), Args = [listKey] }, // WRONGTYPE
        ]);

        Assert.Equal("hello", results[0].AsString());
        Assert.True(results[1].IsError);
    }

    [Fact]
    public async Task ClusterBatchSpanningMultipleShardsPreservesOrderAndRoutesEachCommandCorrectly()
    {
        await using var cluster = await ClusterClient.ConnectAsync(ClusterSeedEndpoints);

        // Deliberately interleaved across all three shards' slot ranges, so a naive
        // implementation that only routed correctly for a single-node batch wouldn't
        // pass — node1: 0-5460, node2: 5461-10922, node3: 10923-16383.
        (int Start, int End)[] ranges = [(0, 5460), (5461, 10922), (10923, 16383), (0, 5460), (10923, 16383), (5461, 10922)];
        var keys = new byte[ranges.Length][];
        var setCommands = new RedisBatchCommand[ranges.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            keys[i] = Encoding.UTF8.GetBytes(FindRandomKeyInSlotRange(ranges[i].Start, ranges[i].End));
            setCommands[i] = new RedisBatchCommand { CommandName = "SET"u8.ToArray(), Args = [keys[i], Encoding.UTF8.GetBytes($"shard-value-{i}")] };
        }

        var setResults = await cluster.ExecuteBatchAsync(setCommands);
        Assert.All(setResults, r => Assert.Equal("OK", r.AsString()));

        var getCommands = new RedisBatchCommand[ranges.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            getCommands[i] = new RedisBatchCommand { CommandName = "GET"u8.ToArray(), Args = [keys[i]] };
        }

        var getResults = await cluster.ExecuteBatchAsync(getCommands);

        for (var i = 0; i < ranges.Length; i++)
        {
            Assert.Equal($"shard-value-{i}", getResults[i].AsString());
        }
    }

    private static string FindRandomKeyInSlotRange(int rangeStart, int rangeEnd)
    {
        for (var i = 0; i < 1_000_000; i++)
        {
            var candidate = $"readus:test:batch:cluster:{Guid.NewGuid():N}";
            if (HashSlot.Compute(Encoding.UTF8.GetBytes(candidate)) is var slot && slot >= rangeStart && slot <= rangeEnd)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not find a candidate key in the target slot range.");
    }
}
