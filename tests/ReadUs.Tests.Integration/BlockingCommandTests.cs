using System.Diagnostics;
using System.Text;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises Tier 2 blocking commands (project spec §4, §13 step 3) against a real
/// standalone <c>redis-server</c> — a disposable Testcontainers-managed instance
/// (project spec §9.2), shared across this whole collection.
/// </summary>
[Collection(StandaloneRedisCollection.Name)]
public class BlockingCommandTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task BlPopReturnsImmediatelyWhenAValueIsAlreadyAvailable()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1, leasedConnectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:blpop:{Guid.NewGuid():N}");

        await client.LpushAsync(key, ["value"u8.ToArray()]);

        var result = await client.BlpopAsync([key], timeout: 5);

        var items = result.AsItems();
        Assert.Equal(2, items.Length);
        Assert.Equal(Encoding.UTF8.GetString(key), items[0].AsString());
        Assert.Equal("value", items[1].AsString());
    }

    [Fact]
    public async Task BlPopHonorsItsServerSideTimeoutAsANullReply()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1, leasedConnectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:blpop:empty:{Guid.NewGuid():N}");

        var result = await client.BlpopAsync([key], timeout: 1);

        Assert.True(result.IsNull);
    }

    [Fact]
    public async Task CancellingAGenuinelyBlockedCommandUnblocksItAndTheConnectionStaysUsable()
    {
        // The real end-to-end version of the race in docs/design/state-machines.md §2:
        // BLPOP actually blocks server-side (nothing will ever be pushed to this key),
        // the client cancels mid-block, CLIENT UNBLOCK has to actually travel over the
        // wire and interrupt it, and the leased connection must come back healthy
        // enough to serve a completely unrelated command afterward.
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1, leasedConnectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:blpop:cancel:{Guid.NewGuid():N}");

        using var cts = new CancellationTokenSource();
        var blockingCall = client.BlpopAsync([key], timeout: 30, cts.Token).AsTask();

        // Give the command time to actually reach the server and enter its blocking
        // wait before cancelling — cancelling too early would just hit the
        // cancel-before-write path already covered by StandaloneClientTests.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockingCall);

        // Cancellation should resolve in well under the command's own 30s server-side
        // timeout — proving CLIENT UNBLOCK actually fired rather than us just waiting
        // out the clock.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {stopwatch.Elapsed}; expected it to be driven by CLIENT UNBLOCK, not the server-side timeout.");

        // The pool's one leased connection must have come back healthy.
        var followUpKey = Encoding.UTF8.GetBytes($"readus:test:blpop:followup:{Guid.NewGuid():N}");
        await client.LpushAsync(followUpKey, ["still-works"u8.ToArray()]);
        var followUp = await client.BlpopAsync([followUpKey], timeout: 5);
        Assert.Equal("still-works", followUp.AsItems()[1].AsString());
    }
}
