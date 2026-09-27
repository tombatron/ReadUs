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
/// Scope deferred out of this pass, per docs/design/state-machines.md §5: explicit
/// per-node pipeline batching (concurrent calls still pipeline within each node's own
/// Tier 1 pool, just not reassembled by this layer). Per-node health tracking and
/// quarantine (design doc §3.1) is implemented via <see cref="ClusterNodeHealthTracker"/>;
/// replica read routing (design doc §3.2) via the <see cref="ReadPreference"/> overload
/// of <see cref="ExecuteAsync(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}[], ReadPreference, CancellationToken)"/>.
/// </summary>
public sealed class ClusterClient : IAsyncDisposable
{
    private const int MaxRedirects = 16;
    private static readonly TimeSpan TryAgainBackoff = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TopologyRefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, GeneratedCommandInfo> CommandsByWireName = BuildCommandLookup();

    private readonly Func<EndPoint, RedisConnectionOptions> _optionsFactory;

    // Lazy<Task<RedisClient>>, not a bare Task<RedisClient>: ConcurrentDictionary.GetOrAdd
    // can invoke its factory more than once under contention, discarding every result
    // but the winner — for a bare Task<RedisClient> factory that means two concurrent
    // first-contacts of the same new node could each open a real RedisClient (its own
    // Tier 1/Tier 2 pool of live sockets), with the loser silently leaked, never
    // disposed. Lazy<T> makes the *dictionary* race harmless the same way (only one
    // constructed Lazy wrapper is ever kept) and additionally guarantees, via its own
    // ExecutionAndPublication mode, that only one caller across any number of
    // concurrent GetOrAdd calls ever actually evaluates the factory and connects.
    private readonly ConcurrentDictionary<string, Lazy<Task<RedisClient>>> _nodeClients = new();
    private readonly ClusterNodeHealthTracker _nodeHealth;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _refreshLoopTask;
    private volatile ClusterTopology _topology;
    private int _roundRobinCounter = -1;

    private ClusterClient(ClusterTopology topology, Func<EndPoint, RedisConnectionOptions> optionsFactory)
    {
        _topology = topology;
        _optionsFactory = optionsFactory;
        _nodeHealth = new ClusterNodeHealthTracker(GetOrCreateNodeClientAsync);
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

    /// <summary>The raw escape hatch (project spec §3), cluster-routed, always against the shard's primary.</summary>
    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        ExecuteAsync(commandName, args, ReadPreference.PrimaryOnly, cancellationToken);

    /// <summary>
    /// Same as <see cref="ExecuteAsync(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}[], CancellationToken)"/>,
    /// with an opt-in per-call read-routing override (design doc §3.2, project spec
    /// §5/§10 — read-preference overrides are deliberately a separate, opt-in overload
    /// rather than changing what the plain one does). Only valid for a command the
    /// vendored table marks read-only; anything else throws <see cref="ArgumentException"/>
    /// before a byte is sent, the same "catch it client-side" shape as the CROSSSLOT
    /// check below.
    /// </summary>
    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, ReadPreference readPreference, CancellationToken cancellationToken = default)
    {
        if (readPreference != ReadPreference.PrimaryOnly && !IsReadOnlyCommand(commandName))
        {
            throw new ArgumentException(
                $"'{Encoding.ASCII.GetString(commandName.Span)}' is not a read-only command; only ReadPreference.PrimaryOnly is valid for it.",
                nameof(readPreference));
        }

        var keys = ExtractKeys(commandName, args);
        var slot = ComputeSlot(keys);
        return ExecuteWithRedirectsAsync(commandName, args, slot, readPreference, cancellationToken);
    }

    /// <summary>
    /// Same shape as <see cref="RedisClient.ExecuteBatchAsync"/>, cluster-routed
    /// (project spec §5: "pipelining means batching per-destination-node, not
    /// per-call"). Each command is independently routed by its own slot through the
    /// same redirect-handling <see cref="ExecuteAsync(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}[], CancellationToken)"/>
    /// path a single call would use — a batch spanning multiple shards therefore fans
    /// out to each shard's own Tier 1 pool and pipelines there as an already-correct,
    /// already-tested emergent property of that routing, rather than this method
    /// duplicating redirect handling for a "batch" special case. Always
    /// <see cref="ReadPreference.PrimaryOnly"/> per item — no per-item read-preference
    /// override in a batch; use the single-command overload for that.
    /// </summary>
    public Task<RedisResult[]> ExecuteBatchAsync(IReadOnlyList<RedisBatchCommand> commands, CancellationToken cancellationToken = default)
    {
        var pending = new Task<RedisResult>[commands.Count];
        for (var i = 0; i < commands.Count; i++)
        {
            pending[i] = ExecuteAsync(commands[i].CommandName, commands[i].Args, cancellationToken).AsTask();
        }

        return Task.WhenAll(pending);
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

        foreach (var lazyClientTask in _nodeClients.Values)
        {
            try
            {
                var client = await lazyClientTask.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // The connection attempt itself failed; nothing was created to dispose.
            }
        }

        await _nodeHealth.DisposeAsync().ConfigureAwait(false);

        _lifetimeCts.Dispose();
    }

    private async ValueTask<RedisResult> ExecuteWithRedirectsAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, int? slot, ReadPreference readPreference, CancellationToken cancellationToken)
    {
        EndPoint? askTarget = null;

        for (var attempt = 0; attempt < MaxRedirects; attempt++)
        {
            EndPoint targetEndPoint;
            bool isReplica;
            if (askTarget is not null)
            {
                // A server-issued ASK/MOVED target is always a primary handling the
                // slot (or its migration) — never influenced by read preference.
                targetEndPoint = askTarget;
                isReplica = false;
            }
            else
            {
                (targetEndPoint, isReplica) = ResolveEndPoint(slot, readPreference);
            }

            if (_nodeHealth.IsQuarantined(targetEndPoint))
            {
                throw new ClusterNodeQuarantinedException(
                    $"Node {targetEndPoint} is quarantined after repeated connection failures; a background probe is reintegrating it.");
            }

            RedisResult reply;
            try
            {
                var client = isReplica
                    ? await GetOrCreateReplicaClientAsync(targetEndPoint).ConfigureAwait(false)
                    : await GetOrCreateNodeClientAsync(targetEndPoint).ConfigureAwait(false);

                if (askTarget is not null)
                {
                    // One-shot per the spec: ASKING is never persisted into the slot map.
                    await client.ExecuteAsync("ASKING"u8.ToArray(), [], cancellationToken).ConfigureAwait(false);
                }

                reply = await client.ExecuteAsync(commandName, args, cancellationToken).ConfigureAwait(false);
            }
            catch (RedisConnectionException)
            {
                _nodeHealth.RecordFailure(targetEndPoint);
                throw;
            }

            _nodeHealth.RecordSuccess(targetEndPoint);

            if (!reply.IsError)
            {
                return reply;
            }

            var error = reply.AsString();

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
        // Quarantined masters last — no reason to spend this refresh's first attempt on
        // a node already known to be down.
        var orderedMasters = _topology.Masters.OrderBy(m => _nodeHealth.IsQuarantined(m.EndPoint) ? 1 : 0);

        foreach (var master in orderedMasters)
        {
            try
            {
                var client = await GetOrCreateNodeClientAsync(master.EndPoint).ConfigureAwait(false);
                var reply = await client.ExecuteAsync("CLUSTER"u8.ToArray(), ["SHARDS"u8.ToArray()], cancellationToken).ConfigureAwait(false);
                _topology = ClusterTopology.Parse(reply);
                _nodeHealth.RecordSuccess(master.EndPoint);
                return;
            }
            catch (RedisConnectionException)
            {
                _nodeHealth.RecordFailure(master.EndPoint);
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

    private (EndPoint EndPoint, bool IsReplica) ResolveEndPoint(int? slot, ReadPreference readPreference)
    {
        if (slot.HasValue)
        {
            if (readPreference != ReadPreference.PrimaryOnly)
            {
                var readTarget = ResolveReadEndPoint(slot.Value, readPreference);
                if (readTarget is not null)
                {
                    return readTarget.Value;
                }
            }

            var owner = _topology.FindOwner(slot.Value);
            if (owner is not null)
            {
                return (owner.EndPoint, false);
            }
        }

        // No key (an admin command) or a slot our map doesn't cover (topology
        // mid-migration) — round-robin across known masters rather than fail outright.
        // Read preference doesn't apply here: there's no single shard to pick a
        // replica "for", so this always stays primary-only.
        var masters = _topology.Masters;
        if (masters.Count == 0)
        {
            throw new RedisConnectionException("No known cluster master nodes.");
        }

        var index = PickHealthyRoundRobinIndex(masters);
        if (index < 0)
        {
            // Every master is quarantined — hand back the round-robin choice anyway;
            // the caller fails fast via the IsQuarantined check in
            // ExecuteWithRedirectsAsync rather than this blocking.
            index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)masters.Count);
        }

        return (masters[index].EndPoint, false);
    }

    /// <summary>
    /// Resolves a non-<see cref="ReadPreference.PrimaryOnly"/> preference against the
    /// shard owning <paramref name="slot"/> (design doc §3.2). Null means "fall back to
    /// the primary" — <see cref="ResolveEndPoint"/> handles that uniformly for both
    /// <see cref="ReadPreference.PreferReplica"/> (no healthy replica) and an
    /// unresolvable slot.
    /// </summary>
    private (EndPoint EndPoint, bool IsReplica)? ResolveReadEndPoint(int slot, ReadPreference readPreference)
    {
        var replicas = _topology.FindReplicas(slot);

        if (readPreference == ReadPreference.RoundRobin)
        {
            var owner = _topology.FindOwner(slot);
            if (owner is null)
            {
                return null;
            }

            var candidates = new ClusterNode[replicas.Count + 1];
            candidates[0] = owner;
            for (var i = 0; i < replicas.Count; i++)
            {
                candidates[i + 1] = replicas[i];
            }

            var index = PickHealthyRoundRobinIndex(candidates);
            if (index < 0)
            {
                index = 0; // every candidate quarantined — fall back to the primary specifically.
            }

            return (candidates[index].EndPoint, index != 0);
        }

        var replicaIndex = PickHealthyRoundRobinIndex(replicas);
        if (replicaIndex >= 0)
        {
            return (replicas[replicaIndex].EndPoint, true);
        }

        if (readPreference == ReadPreference.ReplicaOnly)
        {
            throw new RedisConnectionException(
                $"No healthy replica is available for slot {slot} and ReadPreference.ReplicaOnly was specified.");
        }

        return null; // PreferReplica: caller falls back to the primary.
    }

    /// <summary>The index of the first non-quarantined candidate, starting from the shared round-robin cursor; -1 if every candidate is quarantined (or the list is empty).</summary>
    private int PickHealthyRoundRobinIndex(IReadOnlyList<ClusterNode> candidates)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var index = (int)((uint)Interlocked.Increment(ref _roundRobinCounter) % (uint)candidates.Count);
            if (!_nodeHealth.IsQuarantined(candidates[index].EndPoint))
            {
                return index;
            }
        }

        return -1;
    }

    private Task<RedisClient> GetOrCreateNodeClientAsync(EndPoint endPoint) =>
        GetOrCreateClientAsync(endPoint, _optionsFactory);

    /// <summary>
    /// Same as <see cref="GetOrCreateNodeClientAsync"/>, but for a node this client is
    /// treating as a replica read target: composes a <c>READONLY</c>
    /// <see cref="RedisConnectionOptions.PostConnectAsync"/> hook onto the caller's own
    /// options (design doc §3.2) so every physical connection this client ever opens to
    /// that node — including a pool's self-healing reconnect replacement — is marked
    /// read-only, transparently, with nothing about it visible in the public API.
    /// </summary>
    private Task<RedisClient> GetOrCreateReplicaClientAsync(EndPoint endPoint) =>
        GetOrCreateClientAsync(endPoint, ep => WithReadOnlyPostConnect(_optionsFactory(ep)));

    /// <summary>
    /// Evicts and retries a cached-but-faulted client rather than replaying the same
    /// connection failure forever. Without this, a node that failed its *first*
    /// connection attempt would stay permanently unreachable through this client even
    /// after it recovers. Needed for <see cref="ClusterNodeHealthTracker"/>'s
    /// reintegration probe to mean anything: without eviction, its retries would just
    /// re-observe the same stale fault instead of actually attempting a new connection.
    ///
    /// Stores a <see cref="Lazy{T}"/>, not a bare <c>Task&lt;RedisClient&gt;</c>, so two
    /// concurrent first-contacts of the same new node can't each open a real
    /// <see cref="RedisClient"/> (its own Tier 1/Tier 2 pool of live sockets) only to
    /// have <see cref="ConcurrentDictionary{TKey,TValue}"/> silently discard and leak
    /// one of them — <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>
    /// guarantees at most one caller ever actually evaluates the factory and connects,
    /// regardless of how many concurrently call <c>GetOrAdd</c> for the same key.
    /// </summary>
    private Task<RedisClient> GetOrCreateClientAsync(EndPoint endPoint, Func<EndPoint, RedisConnectionOptions> optionsFactory)
    {
        var key = endPoint.ToString()!;

        while (true)
        {
            var lazy = _nodeClients.GetOrAdd(
                key,
                static (_, state) => new Lazy<Task<RedisClient>>(
                    () => RedisClient.ConnectAsync(state.OptionsFactory(state.EndPoint)),
                    LazyThreadSafetyMode.ExecutionAndPublication),
                (EndPoint: endPoint, OptionsFactory: optionsFactory));

            var task = lazy.Value;
            if (!task.IsFaulted)
            {
                return task;
            }

            ((ICollection<KeyValuePair<string, Lazy<Task<RedisClient>>>>)_nodeClients).Remove(new(key, lazy));
        }
    }

    private static RedisConnectionOptions WithReadOnlyPostConnect(RedisConnectionOptions options)
    {
        var previousHook = options.PostConnectAsync;
        return options with
        {
            PostConnectAsync = async (connection, cancellationToken) =>
            {
                if (previousHook is not null)
                {
                    await previousHook(connection, cancellationToken).ConfigureAwait(false);
                }

                var reply = await connection.SendAsync("READONLY"u8.ToArray(), [], cancellationToken).ConfigureAwait(false);
                if (reply.IsError)
                {
                    throw new RedisConnectionException($"READONLY failed on a replica connection: {reply.AsString()}");
                }
            },
        };
    }

    private static bool IsReadOnlyCommand(ReadOnlyMemory<byte> commandName) =>
        CommandsByWireName.TryGetValue(Encoding.ASCII.GetString(commandName.Span), out var info) && info.IsReadOnly;

    private static (int Slot, EndPoint EndPoint) ParseRedirect(string error)
    {
        var parts = error.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var slot = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var colonIndex = parts[2].LastIndexOf(':');
        var host = parts[2][..colonIndex];
        var port = int.Parse(parts[2][(colonIndex + 1)..], CultureInfo.InvariantCulture);
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

        var name = Encoding.ASCII.GetString(commandName.Span);
        if (!CommandsByWireName.TryGetValue(name, out var info))
        {
            return keys;
        }

        var totalWireLength = 1 + args.Length;
        foreach (var spec in info.KeySpecs)
        {
            var lastWirePosition = spec.LastKeyPosition >= 0
                ? spec.FirstKeyPosition + spec.LastKeyPosition
                : totalWireLength + spec.LastKeyPosition;
            var step = spec.KeyStep <= 0 ? 1 : spec.KeyStep;

            for (var wirePosition = spec.FirstKeyPosition; wirePosition <= lastWirePosition; wirePosition += step)
            {
                var argIndex = wirePosition - 1;
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

        var slot = HashSlot.Compute(keys[0].Span);
        for (var i = 1; i < keys.Count; i++)
        {
            if (HashSlot.Compute(keys[i].Span) != slot)
            {
                throw new ClusterCrossSlotException("This command's key arguments hash to different cluster slots and cannot be executed as a single command.");
            }
        }

        return slot;
    }
}
