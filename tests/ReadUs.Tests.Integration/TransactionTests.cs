using System.Net;
using System.Text;
using ReadUs.Connections;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises the <c>MULTI</c>/<c>WATCH</c>/<c>EXEC</c> transaction builder (project
/// spec §10, §13 step 3) against a real standalone <c>redis-server</c>.
/// </summary>
public class TransactionTests
{
    private static RedisConnectionOptions Options => new()
    {
        EndPoint = new DnsEndPoint("localhost", 6379),
    };

    [Fact]
    public async Task QueuedCommandsExecuteAtomicallyAndReturnEachResult()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1, leasedConnectionCount: 1);
        var key1 = Encoding.UTF8.GetBytes($"readus:test:tx:{Guid.NewGuid():N}");
        var key2 = Encoding.UTF8.GetBytes($"readus:test:tx:{Guid.NewGuid():N}");

        await using var tx = await client.BeginTransactionAsync();
        await tx.MultiAsync();
        await tx.QueueAsync("SET"u8.ToArray(), [key1, "a"u8.ToArray()]);
        await tx.QueueAsync("SET"u8.ToArray(), [key2, "b"u8.ToArray()]);
        await tx.QueueAsync("GET"u8.ToArray(), [key1]);
        var result = await tx.ExecAsync();

        var replies = result.AsItems();
        Assert.Equal(3, replies.Length);
        Assert.Equal("OK", replies[0].AsString());
        Assert.Equal("OK", replies[1].AsString());
        Assert.Equal("a", replies[2].AsString());
    }

    [Fact]
    public async Task WatchAbortsTheTransactionWhenTheWatchedKeyChangesBeforeExec()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 2, leasedConnectionCount: 1);
        var watchedKey = Encoding.UTF8.GetBytes($"readus:test:tx:watch:{Guid.NewGuid():N}");
        await client.SetAsync(watchedKey, "original"u8.ToArray());

        await using var tx = await client.BeginTransactionAsync();
        await tx.WatchAsync([watchedKey]);
        await tx.MultiAsync();
        await tx.QueueAsync("SET"u8.ToArray(), [watchedKey, "from-transaction"u8.ToArray()]);

        // A completely different connection (Tier 1, outside the transaction's leased
        // connection) modifies the watched key before EXEC — this must abort the
        // transaction, per WATCH semantics.
        await client.SetAsync(watchedKey, "modified-elsewhere"u8.ToArray());

        var result = await tx.ExecAsync();

        Assert.True(result.IsNull);

        var finalValue = await client.GetAsync(watchedKey);
        Assert.Equal("modified-elsewhere", finalValue.AsString());
    }

    [Fact]
    public async Task DisposingWithoutExecOrDiscardAbortsDefensively()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1, leasedConnectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:tx:abandoned:{Guid.NewGuid():N}");
        await client.SetAsync(key, "untouched"u8.ToArray());

        await using (var tx = await client.BeginTransactionAsync())
        {
            await tx.MultiAsync();
            await tx.QueueAsync("SET"u8.ToArray(), [key, "should-not-apply"u8.ToArray()]);
            // Disposed without ExecAsync/DiscardAsync.
        }

        var value = await client.GetAsync(key);
        Assert.Equal("untouched", value.AsString());
    }
}
