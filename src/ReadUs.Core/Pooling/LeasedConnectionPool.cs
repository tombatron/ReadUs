using System.Collections.Concurrent;
using ReadUs.Connections;

namespace ReadUs.Pooling;

/// <summary>
/// Tier 2 (project spec §4): connections checked out exclusively for the duration of
/// one logical operation and returned afterward — required for blocking commands and
/// <c>MULTI</c>/<c>WATCH</c>/<c>EXEC</c> transactions, where session affinity to one
/// physical connection is part of correctness, not just an optimization.
///
/// Fixed size for v0 (no growth on demand): <see cref="LeaseAsync"/> blocks until a
/// connection is available rather than opening a new one under pressure. A dead
/// connection returned to the pool (state not <see cref="ConnectionState.Ready"/>,
/// per Invariant I5 / hazard H3) is discarded and lazily replaced the next time it
/// would have been leased out, rather than eagerly reconnected in the background.
/// </summary>
public sealed class LeasedConnectionPool : IAsyncDisposable
{
    private readonly RedisConnectionOptions _options;
    private readonly MultiplexedConnectionPool _controlPool;
    private readonly SemaphoreSlim _availability;
    private readonly ConcurrentQueue<RedisConnection> _idle = new();

    private LeasedConnectionPool(RedisConnectionOptions options, MultiplexedConnectionPool controlPool, RedisConnection[] initial)
    {
        _options = options;
        _controlPool = controlPool;
        _availability = new SemaphoreSlim(initial.Length, initial.Length);

        foreach (var connection in initial)
        {
            _idle.Enqueue(connection);
        }
    }

    /// <param name="size">Number of dedicated connections available to lease.</param>
    /// <param name="controlPoolSize">
    /// Size of the reserved control-connection sub-pool used exclusively for
    /// <c>CLIENT UNBLOCK</c> (hazard H5 — never drawn from the leasable set itself, so
    /// cancellation can never be starved by the very commands it's meant to interrupt).
    /// </param>
    public static async Task<LeasedConnectionPool> CreateAsync(
        RedisConnectionOptions options,
        int size,
        int controlPoolSize = 1,
        CancellationToken cancellationToken = default)
    {
        if (size < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "At least one connection is required.");
        }

        var controlPool = await MultiplexedConnectionPool.CreateAsync(options, controlPoolSize, cancellationToken).ConfigureAwait(false);

        var connections = new RedisConnection[size];
        for (var i = 0; i < size; i++)
        {
            connections[i] = await RedisConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return new LeasedConnectionPool(options, controlPool, connections);
    }

    public async ValueTask<ConnectionLease> LeaseAsync(CancellationToken cancellationToken = default)
    {
        await _availability.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (!_idle.TryDequeue(out var connection))
        {
            // The semaphore count and _idle's occupancy are kept in lockstep by every
            // path that acquires/releases a slot; seeing one without the other means
            // that invariant broke.
            _availability.Release();
            throw new InvalidOperationException("Pool accounting error: an availability slot was granted with no idle connection to match it.");
        }

        if (connection.State != ConnectionState.Ready)
        {
            // Lazily self-heal: a connection returned in a non-Ready state (hazard H3 —
            // never re-queued as Ready) is replaced here rather than eagerly in the
            // background, keeping the pool's steady-state simple at the cost of the
            // occasional lease paying for a fresh handshake.
            await connection.DisposeAsync().ConfigureAwait(false);
            connection = await RedisConnection.ConnectAsync(_options, cancellationToken).ConfigureAwait(false);
        }

        return new ConnectionLease(this, connection);
    }

    internal MultiplexedConnectionPool ControlPool => _controlPool;

    internal void Return(RedisConnection connection)
    {
        // Only a healthy connection goes back into circulation (Invariant I5 / hazard
        // H3); the availability slot is released regardless so the pool's capacity
        // accounting stays correct either way.
        if (connection.State == ConnectionState.Ready)
        {
            _idle.Enqueue(connection);
        }

        _availability.Release();
    }

    public async ValueTask DisposeAsync()
    {
        while (_idle.TryDequeue(out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        await _controlPool.DisposeAsync().ConfigureAwait(false);
    }
}
