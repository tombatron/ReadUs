using System.Net;
using ReadUs.Protocol;

namespace ReadUs.Cluster.Routing;

/// <summary>
/// A snapshot of slot ownership, parsed from a <c>CLUSTER SHARDS</c> reply (project
/// spec §5 — preferred over <c>CLUSTER SLOTS</c>/<c>CLUSTER NODES</c> for its richer,
/// structured shape). Tracks each shard's replicas alongside its master (design doc
/// §3.2 — replica read routing). Immutable — a topology refresh produces a new
/// instance rather than mutating this one, so a caller mid-lookup never observes a
/// half-updated map.
/// </summary>
public sealed class ClusterTopology
{
    private readonly (int Start, int End, ClusterNode Master, ClusterNode[] Replicas)[] _ranges;

    private ClusterTopology(List<(int Start, int End, ClusterNode Master, ClusterNode[] Replicas)> ranges)
    {
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        _ranges = [.. ranges];
    }

    public IReadOnlyList<ClusterNode> Masters => [.. _ranges.Select(r => r.Master).Distinct()];

    /// <summary>
    /// Patches ownership of a single slot (a <c>MOVED</c> reply's slot) without waiting
    /// for a fuller background refresh — project spec §5: "update the local slot map
    /// (at least for that slot)". Splits whichever range currently contains
    /// <paramref name="slot"/> around it rather than rebuilding the whole map.
    /// </summary>
    public ClusterTopology WithSlotOverride(int slot, ClusterNode newOwner)
    {
        var newRanges = new List<(int, int, ClusterNode, ClusterNode[])>(_ranges.Length + 2);

        foreach (var (start, end, master, replicas) in _ranges)
        {
            if (slot < start || slot > end)
            {
                newRanges.Add((start, end, master, replicas));
                continue;
            }

            if (start < slot)
            {
                newRanges.Add((start, slot - 1, master, replicas));
            }

            // The replicas for whichever shard now owns this slot aren't known from a
            // bare MOVED reply (just the new primary's endpoint) — left empty until the
            // next full background refresh fills them in, same tradeoff the original
            // redirect design already made for the primary case.
            newRanges.Add((slot, slot, newOwner, []));

            if (slot < end)
            {
                newRanges.Add((slot + 1, end, master, replicas));
            }
        }

        return new ClusterTopology(newRanges);
    }

    public ClusterNode? FindOwner(int slot) => FindRange(slot)?.Master;

    /// <summary>The replicas of the shard owning <paramref name="slot"/> — empty if the slot isn't covered, or if the shard genuinely has none.</summary>
    public IReadOnlyList<ClusterNode> FindReplicas(int slot) => FindRange(slot)?.Replicas ?? [];

    private (int Start, int End, ClusterNode Master, ClusterNode[] Replicas)? FindRange(int slot)
    {
        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var range = _ranges[mid];
            if (slot < range.Start)
            {
                hi = mid - 1;
            }
            else if (slot > range.End)
            {
                lo = mid + 1;
            }
            else
            {
                return range;
            }
        }

        return null;
    }

    /// <summary>Parses a <c>CLUSTER SHARDS</c> reply: an array of shard maps, each with a "slots" array (start/end pairs, possibly more than one pair per shard) and a "nodes" array of node maps.</summary>
    public static ClusterTopology Parse(RedisResult clusterShardsReply)
    {
        var ranges = new List<(int, int, ClusterNode, ClusterNode[])>();

        foreach (var shard in clusterShardsReply.AsItems())
        {
            var shardFields = shard.AsItems();
            RedisResult slots = default;
            RedisResult nodes = default;

            for (var i = 0; i < shardFields.Length; i += 2)
            {
                var key = shardFields[i].AsString();
                if (key == "slots")
                {
                    slots = shardFields[i + 1];
                }
                else if (key == "nodes")
                {
                    nodes = shardFields[i + 1];
                }
            }

            var (master, replicas) = ParseNodes(nodes);
            if (master is null)
            {
                continue;
            }

            var slotItems = slots.AsItems();
            for (var i = 0; i + 1 < slotItems.Length; i += 2)
            {
                ranges.Add(((int)slotItems[i].AsInt64(), (int)slotItems[i + 1].AsInt64(), master, replicas));
            }
        }

        return new ClusterTopology(ranges);
    }

    private static (ClusterNode? Master, ClusterNode[] Replicas) ParseNodes(RedisResult nodes)
    {
        ClusterNode? master = null;
        var replicas = new List<ClusterNode>();

        foreach (var node in nodes.AsItems())
        {
            var fields = node.AsItems();
            string? id = null;
            string? ip = null;
            string? endpointHost = null;
            long port = 0;
            var isMaster = false;

            for (var i = 0; i < fields.Length; i += 2)
            {
                var key = fields[i].AsString();
                var value = fields[i + 1];
                switch (key)
                {
                    case "id": id = value.AsString(); break;
                    case "ip": ip = value.AsString(); break;
                    case "endpoint": endpointHost = value.AsString(); break;
                    case "port": port = value.AsInt64(); break;
                    case "role": isMaster = value.AsString() == "master"; break;
                }
            }

            // "endpoint" is preferred when known; Redis reports it as an empty string
            // when it can't determine one, in which case "ip" is the documented
            // fallback.
            var host = string.IsNullOrEmpty(endpointHost) ? ip! : endpointHost;
            var clusterNode = new ClusterNode
            {
                Id = id!,
                EndPoint = new DnsEndPoint(host, (int)port),
                IsMaster = isMaster,
            };

            if (isMaster)
            {
                master = clusterNode;
            }
            else
            {
                replicas.Add(clusterNode);
            }
        }

        return (master, [.. replicas]);
    }
}
