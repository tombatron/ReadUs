using ReadUs.Connections;
using ReadUs.Pooling;
using ReadUs.Protocol;

namespace ReadUs;

/// <summary>
/// Top-level standalone client (project spec §10). For now this wraps only the Tier 1
/// multiplexed pool (§13 step 2) — Tier 2 leased connections for blocking commands and
/// transactions land in §13 step 3, and Cluster/Sentinel-transparent routing lands
/// later still. <see cref="ExecuteAsync"/> is the low-level escape hatch from §3; the
/// PingAsync/SetAsync/GetAsync convenience wrappers are hand-written stand-ins for the
/// generated typed command surface that §13 step 4 will produce.
/// </summary>
public sealed class RedisClient : IAsyncDisposable
{
    private readonly MultiplexedConnectionPool _pool;

    private RedisClient(MultiplexedConnectionPool pool) => _pool = pool;

    public static async Task<RedisClient> ConnectAsync(RedisConnectionOptions options, int connectionCount = 4, CancellationToken cancellationToken = default)
    {
        var pool = await MultiplexedConnectionPool.CreateAsync(options, connectionCount, cancellationToken).ConfigureAwait(false);
        return new RedisClient(pool);
    }

    /// <summary>
    /// The raw escape hatch (project spec §3): sends any command, including ones the
    /// future generated surface doesn't know about yet, and module commands
    /// (<c>FT.*</c>, <c>JSON.*</c>, <c>BF.*</c>, ...).
    /// </summary>
    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        _pool.ExecuteAsync(commandName, args, cancellationToken);

    public ValueTask<RedisResult> PingAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.Ping, [], cancellationToken);

    public ValueTask<RedisResult> SetAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.Set, [key, value], cancellationToken);

    public ValueTask<RedisResult> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.Get, [key], cancellationToken);

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}
