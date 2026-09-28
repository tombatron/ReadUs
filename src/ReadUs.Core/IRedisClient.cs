using ReadUs.Protocol;

namespace ReadUs;

/// <summary>
/// The uniform command-execution shape project spec §10 asks for: "a top-level
/// `IRedisClient`... abstraction with the same shape for standalone, Cluster, and
/// Sentinel-discovered connections" so application code never needs an
/// <c>if (cluster) {...} else {...}</c> branch for an ordinary command.
/// <see cref="RedisClient"/>, <c>ClusterClient</c> (<c>ReadUs.Cluster</c>), and
/// <c>SentinelClient</c> (<c>ReadUs.Sentinel</c>) all implement it.
///
/// Deliberately narrow — only members with truly identical shape and semantics across
/// all three today. Spec §10 itself draws this line: "Cluster/Sentinel-*specific*
/// concerns... live behind clearly separate, opt-in surface." Two real members are
/// excluded on exactly that basis, not overlooked: <c>ClusterClient</c>'s
/// <c>ReadPreference</c>-overloaded <c>ExecuteAsync</c> (a Cluster-only concept), and
/// <c>BeginTransactionAsync</c> (a Cluster transaction must pick a node — via a routing
/// key — before <c>WATCH</c> even runs, since <c>RedisTransaction</c> leases its
/// connection immediately at <c>StartAsync</c>; <c>RedisClient</c>/<c>SentinelClient</c>
/// need no such key, so the two shapes genuinely don't unify without either forcing a
/// meaningless parameter onto the single-node types or having <c>ClusterClient</c>
/// throw <see cref="NotSupportedException"/> from an interface member — both worse than
/// just keeping it a concrete, type-specific method). See
/// docs/design/state-machines.md, "Recorded during implementation of IRedisClient
/// unification," for the full reasoning.
/// </summary>
public interface IRedisClient : IAsyncDisposable
{
    /// <summary>The raw escape hatch (project spec §3): sends any non-blocking command, including module commands and anything the generated typed surface doesn't cover yet.</summary>
    ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default);

    /// <summary>The raw escape hatch for <c>BLOCKING</c>-flagged commands (project spec §4).</summary>
    ValueTask<RedisResult> ExecuteBlockingAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default);

    /// <summary>Fires every command in <paramref name="commands"/> without waiting for an earlier one's reply first, returning all results in the same order (project spec §5/§8).</summary>
    Task<RedisResult[]> ExecuteBatchAsync(IReadOnlyList<RedisBatchCommand> commands, CancellationToken cancellationToken = default);
}
