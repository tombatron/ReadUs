using System.Net;

namespace ReadUs.Cluster.Routing;

/// <summary>One node of a cluster shard, as reported by <c>CLUSTER SHARDS</c>.</summary>
public sealed class ClusterNode
{
    public required string Id { get; init; }

    public required EndPoint EndPoint { get; init; }

    public required bool IsMaster { get; init; }

    /// <summary>A stable string for pool-keying and diagnostics — <c>DnsEndPoint.ToString()</c> already does exactly this ("host:port"), kept as a named property so callers don't need to know that.</summary>
    public string Key => EndPoint.ToString()!;
}
