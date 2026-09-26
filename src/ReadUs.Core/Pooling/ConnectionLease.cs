using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Pooling;

/// <summary>
/// A Tier 2 connection checked out exclusively for one logical operation. Returning it
/// (via <see cref="DisposeAsync"/>) is what makes it available to the next lease —
/// callers should scope it with <c>await using</c>.
/// </summary>
public readonly struct ConnectionLease : IAsyncDisposable
{
    private readonly LeasedConnectionPool _pool;

    internal ConnectionLease(LeasedConnectionPool pool, RedisConnection connection)
    {
        _pool = pool;
        Connection = connection;
    }

    public RedisConnection Connection { get; }

    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        Connection.SendAsync(commandName, args, cancellationToken);

    /// <summary>Sends a <c>BLOCKING</c>-flagged command; see docs/design/state-machines.md §2.</summary>
    public ValueTask<RedisResult> ExecuteBlockingAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        Connection.SendBlockingAsync(commandName, args, _pool.ControlPool, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _pool.Return(Connection);
        return ValueTask.CompletedTask;
    }
}
