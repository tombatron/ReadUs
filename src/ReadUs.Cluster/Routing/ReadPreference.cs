namespace ReadUs.Cluster.Routing;

/// <summary>
/// Per-call read routing (project spec §5, design doc §3.2). Opt-in — the ordinary
/// <see cref="ClusterClient.ExecuteAsync(ReadOnlyMemory{byte}, ReadOnlyMemory{byte}[], System.Threading.CancellationToken)"/>
/// overload always behaves as <see cref="PrimaryOnly"/> and never changes.
/// </summary>
public enum ReadPreference
{
    /// <summary>Always the shard's primary. The only valid choice for a write command.</summary>
    PrimaryOnly,

    /// <summary>A healthy replica if the shard has one; falls back to the primary otherwise.</summary>
    PreferReplica,

    /// <summary>A healthy replica only — throws rather than falling back to the primary if none is available.</summary>
    ReplicaOnly,

    /// <summary>Round-robins across the shard's primary and all its replicas, skipping quarantined candidates.</summary>
    RoundRobin,
}
