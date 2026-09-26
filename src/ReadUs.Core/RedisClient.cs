using ReadUs.Connections;
using ReadUs.Pooling;
using ReadUs.Protocol;
using ReadUs.Transactions;

namespace ReadUs;

/// <summary>
/// Top-level standalone client (project spec §10). Wraps both the Tier 1 multiplexed
/// pool (§13 step 2) and the Tier 2 leased pool (§13 step 3) — Cluster/Sentinel-
/// transparent routing lands later still.
///
/// <see cref="ExecuteAsync"/>/<see cref="ExecuteBlockingAsync"/> are the low-level
/// escape hatches from project spec §3 — for anything the generated surface doesn't
/// cover yet, and module commands (<c>FT.*</c>, <c>JSON.*</c>, <c>BF.*</c>, ...). Typed
/// command methods (<c>GetAsync</c>, <c>SetAsync</c>, <c>BlpopAsync</c>, ...) are
/// generated onto this type as extension methods by <c>ReadUs.SourceGenerators</c> from
/// the vendored command table (project spec §13 step 4) — see the
/// <c>ReadUs.Generated</c> namespace and codegen/redis-commands/SOURCE.md.
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
    /// Tier 1 pool, including ones the generated surface doesn't know about yet, and
    /// module commands.
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

    public async ValueTask DisposeAsync()
    {
        await _multiplexedPool.DisposeAsync().ConfigureAwait(false);
        await _leasedPool.DisposeAsync().ConfigureAwait(false);
    }
}
