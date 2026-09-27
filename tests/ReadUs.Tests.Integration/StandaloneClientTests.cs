using System.Text;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises the Tier 1 multiplexed pool (project spec §13 step 2) against a real
/// standalone <c>redis-server</c> — a disposable Testcontainers-managed instance
/// (project spec §9.2), shared across this whole collection.
/// </summary>
[Collection(StandaloneRedisCollection.Name)]
public class StandaloneClientTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task PingReturnsPong()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        var result = await client.PingAsync();

        Assert.Equal("PONG", result.AsString());
    }

    [Fact]
    public async Task SetThenGetRoundTripsTheValue()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:{Guid.NewGuid():N}");
        var value = "hello from ReadUs"u8.ToArray();

        var setResult = await client.SetAsync(key, value);
        Assert.Equal("OK", setResult.AsString());

        var getResult = await client.GetAsync(key);
        Assert.Equal("hello from ReadUs", getResult.AsString());
    }

    [Fact]
    public async Task GetOfMissingKeyReturnsNull()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:missing:{Guid.NewGuid():N}");

        var result = await client.GetAsync(key);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task ConcurrentRequestsOnASharedPoolAllCompleteWithCorrectResults()
    {
        // Drives many concurrent logical callers through a small pool of multiplexed
        // connections, verifying FIFO reply correlation holds under real contention —
        // not just in the single-threaded unit tests for the frame reader.
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 4);

        var tasks = new Task[200];
        for (var i = 0; i < tasks.Length; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                var key = Encoding.UTF8.GetBytes($"readus:test:concurrent:{index}:{Guid.NewGuid():N}");
                var value = Encoding.UTF8.GetBytes($"value-{index}");

                await client.SetAsync(key, value);
                var result = await client.GetAsync(key);

                Assert.Equal($"value-{index}", result.AsString());
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task RawExecuteAsyncReachesCommandsWithoutAConvenienceWrapper()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        var result = await client.ExecuteAsync("ECHO"u8.ToArray(), ["hi there"u8.ToArray()]);

        Assert.Equal("hi there", result.AsString());
    }

    [Fact]
    public async Task CancellingBeforeTheWriteHappensDoesNotBreakTheConnectionForSubsequentCommands()
    {
        // Covers only the "cancelled before a byte was written" path, which SendAsync
        // handles by never touching the connection at all. The much harder race —
        // cancel lands after the command is already on the wire, concurrently with the
        // real reply arriving — is exactly what the exhaustive state-space walker
        // (docs/design/state-machines.md §2.6, project spec §13 step 3) is for; it
        // isn't something a live-socket integration test can force deterministically.
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.ExecuteAsync("PING"u8.ToArray(), [], cts.Token));

        var followUp = await client.PingAsync();
        Assert.Equal("PONG", followUp.AsString());
    }
}
