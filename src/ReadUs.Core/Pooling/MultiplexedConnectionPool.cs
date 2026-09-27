using ReadUs.Connections;
using ReadUs.Diagnostics;
using ReadUs.Protocol;

namespace ReadUs.Pooling;

/// <summary>
/// Tier 1 (project spec §4): a small fixed set of multiplexed physical connections
/// used for all non-blocking request/response traffic. Routing is plain round-robin
/// for v0 — scaling connection count with configured concurrency rather than per
/// command, per the spec, but not yet load-aware (least-pending / latency-aware
/// routing is a later optimization once benchmarks justify the added complexity).
///
/// Self-healing (project spec §7 — "cross-AZ/cross-region latency and transient
/// managed-service blips are the norm"): a background loop watches for faulted slots
/// and replaces them with a fresh connection under backoff-with-jitter, so a transient
/// blip on managed Redis doesn't permanently wedge that slot for the pool's lifetime.
/// <see cref="Rent"/> also skips a currently-faulted slot in favor of the next healthy
/// one rather than handing back a connection it already knows is dead.
/// </summary>
public sealed class MultiplexedConnectionPool : IAsyncDisposable, IControlChannel
{
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InitialReconnectBackoff = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(10);

    private readonly RedisConnection[] _connections;
    private readonly RedisConnectionOptions _options;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<int> _reconnecting = [];
    private readonly Lock _reconnectingLock = new();
    private readonly Task _healthLoopTask;

    private int _nextIndex = -1;

    private MultiplexedConnectionPool(RedisConnection[] connections, RedisConnectionOptions options)
    {
        _connections = connections;
        _options = options;
        _healthLoopTask = Task.Run(HealthLoopAsync);
    }

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

        return new MultiplexedConnectionPool(connections, options);
    }

    /// <summary>
    /// Round-robins starting from the next slot, but skips over any slot it can see is
    /// currently faulted in favor of the next healthy one — a background reconnect for
    /// that slot is already in flight (see <see cref="HealthLoopAsync"/>) regardless of
    /// whether any caller happens to land on it.
    /// </summary>
    public RedisConnection Rent()
    {
        int start = (int)((uint)Interlocked.Increment(ref _nextIndex) % (uint)_connections.Length);

        for (int i = 0; i < _connections.Length; i++)
        {
            int index = (start + i) % _connections.Length;
            var candidate = Volatile.Read(ref _connections[index]);
            if (candidate.State == ConnectionState.Ready)
            {
                return candidate;
            }
        }

        // Every slot is currently unhealthy. Hand back the round-robin choice anyway —
        // its SendAsync will throw a clear RedisConnectionException rather than this
        // call silently blocking, and reconnects are already in flight for all of them.
        return Volatile.Read(ref _connections[start]);
    }

    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        Rent().SendAsync(commandName, args, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _healthLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _lifetimeCts.Dispose();
    }

    private async Task HealthLoopAsync()
    {
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                for (int i = 0; i < _connections.Length; i++)
                {
                    var connection = Volatile.Read(ref _connections[i]);
                    if (connection.State is ConnectionState.Faulted or ConnectionState.Closed)
                    {
                        StartReconnectIfNotAlready(i);
                    }
                }

                await Task.Delay(HealthCheckInterval, _lifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartReconnectIfNotAlready(int index)
    {
        lock (_reconnectingLock)
        {
            if (!_reconnecting.Add(index))
            {
                return;
            }
        }

        _ = ReconnectSlotAsync(index);
    }

    private async Task ReconnectSlotAsync(int index)
    {
        try
        {
            var backoff = InitialReconnectBackoff;

            while (!_lifetimeCts.IsCancellationRequested)
            {
                try
                {
                    var replacement = await RedisConnection.ConnectAsync(_options, _lifetimeCts.Token).ConfigureAwait(false);
                    var previous = Volatile.Read(ref _connections[index]);
                    Volatile.Write(ref _connections[index], replacement);
                    ReadUsDiagnostics.Reconnected();

                    // Deliberately fire-and-forget: `previous` is already Faulted/Closed
                    // (that's why it's being replaced), so this is just releasing its
                    // socket/read-loop-task bookkeeping — nothing downstream is waiting
                    // on it, and there's no reason to delay handing back the new
                    // connection until that cleanup finishes.
#pragma warning disable CA2012
                    _ = previous.DisposeAsync();
#pragma warning restore CA2012
                    return;
                }
                catch when (!_lifetimeCts.IsCancellationRequested)
                {
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)backoff.TotalMilliseconds));
                    try
                    {
                        await Task.Delay(backoff + jitter, _lifetimeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, MaxReconnectBackoff.TotalMilliseconds));
                }
            }
        }
        finally
        {
            lock (_reconnectingLock)
            {
                _reconnecting.Remove(index);
            }
        }
    }
}
