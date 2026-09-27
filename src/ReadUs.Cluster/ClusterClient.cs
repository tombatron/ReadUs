using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using ReadUs.Cluster.Routing;
using ReadUs.Connections;
using ReadUs.Diagnostics;
using ReadUs.Generated;
using ReadUs.Protocol;

namespace ReadUs.Cluster;

/// <summary>
/// Cluster-aware command execution (project spec §5): routes by slot, follows
/// <c>MOVED</c>/<c>ASK</c>/<c>TRYAGAIN</c>, surfaces <c>CLUSTERDOWN</c> distinctly, and
/// validates multi-key commands client-side before ever sending them (the
/// <c>CROSSSLOT</c> pre-flight check). One <see cref="RedisClient"/> (with its own
/// Tier 1/Tier 2 pools) is kept per discovered master node — routing is a layer on top
/// of the existing connection machinery, not a second one.
///
/// Scope deferred out of this pass, per docs/design/state-machines.md §5: replica read
/// routing (primary-only for now — the spec's own safest default), per-node health
/// tracking/quarantine, and explicit per-node pipeline batching (concurrent calls still
/// pipeline within each node's own Tier 1 pool, just not reassembled by this layer).
/// </summary>
public sealed class ClusterClient : IAsyncDisposable
{
    private const int MaxRedirects = 16;
    private static readonly TimeSpan TryAgainBackoff = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TopologyRefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, GeneratedCommandInfo> CommandsByWireName = BuildCommandLookup();

    private readonly Func<EndPoint, RedisConnectionOptions> _optionsFactory;
    private readonly ConcurrentDictionary<string, Task<RedisClient>> _nodeClients = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _refreshLoopTask;
    private volatile ClusterTopology _topology;
    private int _roundRobinCounter = -1;

    private ClusterClient(ClusterTopology topology, Func<EndPoint, RedisConnectionOptions> optionsFactory)
    {
        _topology = topology;
        _optionsFactory = optionsFactory;
        _refreshLoopTask = Task.Run(RefreshLoopAsync);
    }

    public static async Task<ClusterClient> ConnectAsync(
        IReadOnlyList<EndPoint> seedEndpoints,
        Func<EndPoint, RedisConnectionOptions>? optionsFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (seedEndpoints.Count == 0)
        {
            throw new ArgumentException("At least one seed endpoint is required.", nameof(seedEndpoints));
        }

        optionsFactory ??= static endpoint => new RedisConnectionOptions { EndPoint = endpoint };

        Exception? lastError = null;
        foreach (var seed in seedEndpoints)
        {
            try
            {
                var topology = await DiscoverTopologyAsync(seed, optionsFactory, cancellationToken).ConfigureAwait(false);
                return new ClusterClient(topology, optionsFactory);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new RedisConnectionException("Could not discover cluster topology from any seed endpoint.", lastError!);
    }

    /// <summary>The raw escape hatch (project spec §3), cluster-routed.</summary>
    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default)
    {
        var keys = ExtractKeys(commandName, args);
        int? slot = ComputeSlot(keys);
        return ExecuteWithRedirectsAsync(commandName, args, slot, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _refreshLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var clientTask in _nodeClients.Values)
        {
            try
            {
                var client = await clientTask.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The connection attempt itself failed; nothing was created to dispose.
            }
        }

        _lifetimeCts.Dispose();
    }

    private async ValueTask<RedisResult> ExecuteWithRedirectsAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, int? slot, CancellationToken cancellationToken)
    {
        EndPoint? askTarget = null;

        for (int attempt = 0; attempt < MaxRedirects; attempt++)
        {
            var targetEndPoint = askTarget ?? ResolveEndPoint(slot);
            var client = await GetOrCreateNodeClientAsync(targetEndPoint).ConfigureAwait(false);

            if (askTarget is not null)
            {
                // One-shot per the spec: ASKING is never persisted into the slot map.
                await client.ExecuteAsync("ASKING"u8.ToArray(), [], cancellationToken).ConfigureAwait(false);
            }

            var reply = await client.ExecuteAsync(commandName, args, cancellationToken).ConfigureAwait(false);
            if (!reply.IsError)
            {
                return reply;
            }

            string error = reply.AsString();

            if (error.StartsWith("MOVED ", StringComparison.Ordinal))
            {
                ReadUsDiagnostics.ClusterRedirect("moved");
                var (movedSlot, endpoint) = ParseRedirect(error);
                UpdateSlotOwner(movedSlot, endpoint);
                askTarget = null;
                continue;
            }

            if (error.StartsWith("ASK ", StringComparison.Ordinal))
            {
                ReadUsDiagnostics.ClusterRedirect("ask");
                (_, askTarget) = ParseRedirect(error);
                continue;
            }

            if (error.StartsWith("TRYAGAIN", StringComparison.Ordinal))
            {
                ReadUsDiagnostics.ClusterRedirect("tryagain");
                await Task.Delay(TryAgainBackoff, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (error.StartsWith("CLUSTERDOWN", StringComparison.Ordinal))
            {
                ReadUsDiagnostics.ClusterRedirect("clusterdown");
                throw new ClusterDownException(error);
            }

            // An ordinary command-level error (unrelated to routing) — hand it back
            // exactly as a non-cluster ExecuteAsync would.
            return reply;
        }

        throw new ClusterTooManyRedirectsException(
            $"Gave up after {MaxRedirects} redirects for a command targeting slot {(slot?.ToString(CultureInfo.InvariantCulture) ?? "(none)")}.");
    }

    private static async Task<ClusterTopology> DiscoverTopologyAsync(EndPoint seed, Func<EndPoint, RedisConnectionOptions> optionsFactory, CancellationToken cancellationToken)
    {
        await using var seedClient = await RedisClient.ConnectAsync(optionsFactory(seed), connectionCount: 1, leasedConnectionCount: 1, cancellationToken).ConfigureAwait(false);
        var reply = await seedClient.ExecuteAsync("CLUSTER"u8.ToArray(), ["SHARDS"u8.ToArray()], cancellationToken).ConfigureAwait(false);
        return ClusterTopology.Parse(reply);
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(TopologyRefreshInterval, _lifetimeCts.Token).ConfigureAwait(false);
                await RefreshTopologyAsync(_lifetimeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshTopologyAsync(CancellationToken cancellationToken)
    {
        foreach (var master in _topology.Masters)
        {
            try
            {
                var client = await GetOrCreateNodeClientAsync(master.EndPoint).ConfigureAwait(false);
                var reply = await client.ExecuteAsync("CLUSTER"u8.ToArray(), ["SHARDS"u8.ToArray()], cancellationToken).ConfigureAwait(false);
                _topology = ClusterTopology.Parse(reply);
                return;
            }
            catch
            {
                // Try the next known master; if all of them are unreachable the stale
                // topology is kept rather than discarded.
            }
        }
    }

    private void UpdateSlotOwner(int slot, EndPoint endPoint)
    {
        var newOwner = new ClusterNode { Id = string.Empty, EndPoint = endPoint, IsMaster = true };
        _topology = _topology.WithSlotOverride(slot, newOwner);

        // Fire-and-forget fuller refresh (project spec §5) — the surgical single-slot
        // patch above is enough to make progress on the command that triggered it.
        _ = RefreshTopologyAsync(CancellationToken.None);
    }

    private EndPoint ResolveEndPoint(int? slot)
    {
        if (slot.HasValue)
        {
            var owner = _topology.FindOwner(slot.Value);
            if (owner is not null)
            {
                return owner.EndPoint;
            }
        }

        // No key (an admin command) or a slot our map doesn't cover (topology
        // mid-migration) — round-robin across known masters rather than fail outright.
        var masters = _topology.Masters;
        if (masters.Count == 0)
        {
            throw new RedisConnectionException("No known cluster master nodes.");
        }

        int index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)masters.Count);
        return masters[index].EndPoint;
    }

    private Task<RedisClient> GetOrCreateNodeClientAsync(EndPoint endPoint) =>
        _nodeClients.GetOrAdd(endPoint.ToString()!, _ => RedisClient.ConnectAsync(_optionsFactory(endPoint)));

    private static (int Slot, EndPoint EndPoint) ParseRedirect(string error)
    {
        var parts = error.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int slot = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int colonIndex = parts[2].LastIndexOf(':');
        string host = parts[2][..colonIndex];
        int port = int.Parse(parts[2][(colonIndex + 1)..], CultureInfo.InvariantCulture);
        return (slot, new DnsEndPoint(host, port));
    }

    private static Dictionary<string, GeneratedCommandInfo> BuildCommandLookup()
    {
        var dict = new Dictionary<string, GeneratedCommandInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in CommandMetadata.All)
        {
            // Subcommands (CLIENT/CLUSTER/SENTINEL/...) share their container's wire
            // name and never carry key specs in the vendored table — top-level entries
            // are all that's needed for routing purposes.
            if (command.Container is null)
            {
                dict[command.WireCommandName] = command;
            }
        }

        return dict;
    }

    private static List<ReadOnlyMemory<byte>> ExtractKeys(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args)
    {
        var keys = new List<ReadOnlyMemory<byte>>();

        string name = Encoding.ASCII.GetString(commandName.Span);
        if (!CommandsByWireName.TryGetValue(name, out var info))
        {
            return keys;
        }

        int totalWireLength = 1 + args.Length;
        foreach (var spec in info.KeySpecs)
        {
            int lastWirePosition = spec.LastKeyPosition >= 0
                ? spec.FirstKeyPosition + spec.LastKeyPosition
                : totalWireLength + spec.LastKeyPosition;
            int step = spec.KeyStep <= 0 ? 1 : spec.KeyStep;

            for (int wirePosition = spec.FirstKeyPosition; wirePosition <= lastWirePosition; wirePosition += step)
            {
                int argIndex = wirePosition - 1;
                if (argIndex >= 0 && argIndex < args.Length)
                {
                    keys.Add(args[argIndex]);
                }
            }
        }

        return keys;
    }

    private static int? ComputeSlot(List<ReadOnlyMemory<byte>> keys)
    {
        if (keys.Count == 0)
        {
            return null;
        }

        int slot = HashSlot.Compute(keys[0].Span);
        for (int i = 1; i < keys.Count; i++)
        {
            if (HashSlot.Compute(keys[i].Span) != slot)
            {
                throw new ClusterCrossSlotException("This command's key arguments hash to different cluster slots and cannot be executed as a single command.");
            }
        }

        return slot;
    }
}
