using System.Collections.Concurrent;
using System.Net;
using ReadUs.Connections;

namespace ReadUs.Cluster.Routing;

/// <summary>
/// Per-node health state for <see cref="ClusterClient"/> (design doc §3.1): tracks
/// consecutive connection failures per node, quarantines a node that crosses the
/// threshold, and runs a background prober that reintegrates it once a bare
/// <c>PING</c> succeeds again. Deliberately independent of the node's own
/// <see cref="RedisClient"/>/Tier 1 pool, which already self-heals its individual
/// connections — this tracks whether the *cluster layer* keeps routing to the node
/// at all.
/// </summary>
public sealed class ClusterNodeHealthTracker(Func<EndPoint, Task<RedisClient>> getClient) : IAsyncDisposable
{
    // Deferred decision (design doc §5 tracking): fixed constants, not yet part of
    // RedisConnectionOptions/a cluster-specific options type. Same status as
    // RedisConnection.UnblockReconciliationGrace — simplest thing that let this be
    // written and tested, revisit once cluster configuration solidifies.
    private const int ConsecutiveFailureThreshold = 3;
    private static readonly TimeSpan InitialProbeBackoff = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxProbeBackoff = TimeSpan.FromSeconds(10);

    private sealed class NodeState
    {
        public int ConsecutiveFailures;
        public int QuarantinedFlag;
    }

    private readonly Func<EndPoint, Task<RedisClient>> _getClient = getClient;
    private readonly ConcurrentDictionary<string, NodeState> _states = new();
    private readonly ConcurrentDictionary<string, Task> _proberTasks = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    public bool IsQuarantined(EndPoint endpoint) =>
        _states.TryGetValue(Key(endpoint), out var state) && Volatile.Read(ref state.QuarantinedFlag) == 1;

    /// <summary>
    /// Resets the failure counter. Deliberately never clears quarantine itself — only
    /// <see cref="ProbeLoopAsync"/>'s own dedicated <c>PING</c> does that (see design
    /// doc §3.1's note on why an ordinary caller's success can't be trusted to mean
    /// the same thing as a background probe's).
    /// </summary>
    public void RecordSuccess(EndPoint endpoint)
    {
        var state = _states.GetOrAdd(Key(endpoint), static _ => new NodeState());
        Volatile.Write(ref state.ConsecutiveFailures, 0);
    }

    public void RecordFailure(EndPoint endpoint)
    {
        var state = _states.GetOrAdd(Key(endpoint), static _ => new NodeState());
        var failures = Interlocked.Increment(ref state.ConsecutiveFailures);

        if (failures >= ConsecutiveFailureThreshold && Interlocked.CompareExchange(ref state.QuarantinedFlag, 1, 0) == 0)
        {
            // Exactly-once claim (same shape as PendingRequest's completion claim,
            // design doc §2.4): whichever concurrent caller's failure happens to cross
            // the threshold starts the one prober loop for this node.
            var endpointCopy = endpoint;
            _proberTasks[Key(endpoint)] = ProbeLoopAsync(endpointCopy, state);
        }
    }

    private async Task ProbeLoopAsync(EndPoint endpoint, NodeState state)
    {
        var key = Key(endpoint);
        var backoff = InitialProbeBackoff;

        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
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

                try
                {
                    var client = await _getClient(endpoint).ConfigureAwait(false);
                    await client.ExecuteAsync("PING"u8.ToArray(), [], _lifetimeCts.Token).ConfigureAwait(false);

                    Volatile.Write(ref state.ConsecutiveFailures, 0);
                    Volatile.Write(ref state.QuarantinedFlag, 0);
                    return;
                }
                catch (RedisConnectionException)
                {
                    backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, MaxProbeBackoff.TotalMilliseconds));
                }
            }
        }
        finally
        {
            _proberTasks.TryRemove(key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        foreach (var task in _proberTasks.Values)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetimeCts.Dispose();
    }

    private static string Key(EndPoint endpoint) => endpoint.ToString()!;
}
