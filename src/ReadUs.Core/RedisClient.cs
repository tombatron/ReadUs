using ReadUs.Connections;
using ReadUs.Pooling;
using ReadUs.Protocol;
using ReadUs.Transactions;

namespace ReadUs;

/// <summary>
/// Top-level standalone client (project spec §10). Wraps both the Tier 1 multiplexed
/// pool (§13 step 2) and the Tier 2 leased pool (§13 step 3) — Cluster/Sentinel-
/// transparent routing lands later still. <see cref="ExecuteAsync"/> is the low-level
/// escape hatch from §3; the Ping/Set/Get/BlPop convenience wrappers are hand-written
/// stand-ins for the generated typed command surface that §13 step 4 will produce.
/// </summary>
public sealed class RedisClient : IAsyncDisposable
{
    private readonly MultiplexedConnectionPool _multiplexedPool;
    private readonly LeasedConnectionPool _leasedPool;

    private RedisClient(MultiplexedConnectionPool multiplexedPool, LeasedConnectionPool leasedPool)
    {
        _multiplexedPool = multiplexedPool;
        _leasedPool = leasedPool;
    }

    public static async Task<RedisClient> ConnectAsync(
        RedisConnectionOptions options,
        int connectionCount = 4,
        int leasedConnectionCount = 4,
        CancellationToken cancellationToken = default)
    {
        var multiplexedPool = await MultiplexedConnectionPool.CreateAsync(options, connectionCount, cancellationToken).ConfigureAwait(false);
        var leasedPool = await LeasedConnectionPool.CreateAsync(options, leasedConnectionCount, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new RedisClient(multiplexedPool, leasedPool);
    }

    /// <summary>
    /// The raw escape hatch (project spec §3): sends any non-blocking command over the
    /// Tier 1 pool, including ones the future generated surface doesn't know about yet,
    /// and module commands (<c>FT.*</c>, <c>JSON.*</c>, <c>BF.*</c>, ...).
    /// </summary>
    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        _multiplexedPool.ExecuteAsync(commandName, args, cancellationToken);

    /// <summary>
    /// The raw escape hatch for <c>BLOCKING</c>-flagged commands: leases a Tier 2
    /// connection for the duration of the call and returns it afterward. Cancellation
    /// drives the <c>CLIENT UNBLOCK</c> reconciliation protocol (docs/design/state-machines.md §2)
    /// rather than merely abandoning the wait.
    /// </summary>
    public async ValueTask<RedisResult> ExecuteBlockingAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default)
    {
        await using var lease = await _leasedPool.LeaseAsync(cancellationToken).ConfigureAwait(false);
        return await lease.ExecuteBlockingAsync(commandName, args, cancellationToken).ConfigureAwait(false);
    }

    public Task<RedisTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        RedisTransaction.StartAsync(_leasedPool, cancellationToken);

    public ValueTask<RedisResult> PingAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.Ping, [], cancellationToken);

    public ValueTask<RedisResult> SetAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.Set, [key, value], cancellationToken);

    public ValueTask<RedisResult> GetAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.Get, [key], cancellationToken);

    /// <summary>
    /// <c>BLPOP key [key ...] timeout</c> — blocks server-side for up to
    /// <paramref name="timeoutSeconds"/> (0 = block indefinitely), independently
    /// cancellable via <paramref name="cancellationToken"/> per project spec §4.
    /// </summary>
    public ValueTask<RedisResult> BlPopAsync(ReadOnlyMemory<byte> key, double timeoutSeconds, CancellationToken cancellationToken = default) =>
        ExecuteBlockingAsync(CommandNames.BlPop, [key, TimeoutArg(timeoutSeconds)], cancellationToken);

    public ValueTask<RedisResult> LPushAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
        ExecuteAsync(CommandNames.LPush, [key, value], cancellationToken);

    private static byte[] TimeoutArg(double timeoutSeconds) =>
        System.Text.Encoding.ASCII.GetBytes(timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async ValueTask DisposeAsync()
    {
        await _multiplexedPool.DisposeAsync().ConfigureAwait(false);
        await _leasedPool.DisposeAsync().ConfigureAwait(false);
    }
}
