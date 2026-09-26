using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Pooling;

/// <summary>
/// Tier 1 (project spec §4): a small fixed set of multiplexed physical connections
/// used for all non-blocking request/response traffic. Routing is plain round-robin
/// for v0 — scaling connection count with configured concurrency rather than per
/// command, per the spec, but not yet load-aware (least-pending / latency-aware
/// routing is a later optimization once benchmarks justify the added complexity).
/// </summary>
public sealed class MultiplexedConnectionPool : IAsyncDisposable
{
    private readonly RedisConnection[] _connections;
    private int _nextIndex = -1;

    private MultiplexedConnectionPool(RedisConnection[] connections) => _connections = connections;

    public static async Task<MultiplexedConnectionPool> CreateAsync(RedisConnectionOptions options, int size, CancellationToken cancellationToken = default)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "At least one connection is required.");
        }

        var connections = new RedisConnection[size];
        for (int i = 0; i < size; i++)
        {
            connections[i] = await RedisConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return new MultiplexedConnectionPool(connections);
    }

    public RedisConnection Rent()
    {
        int index = (int)((uint)Interlocked.Increment(ref _nextIndex) % (uint)_connections.Length);
        return _connections[index];
    }

    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        Rent().SendAsync(commandName, args, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
