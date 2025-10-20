using System.Collections;
using ReadUs.Parser;

namespace ReadUs.Commands.ResultModels;

public sealed class ClusterShardsResult(ParseResult result) : IReadOnlyCollection<ClusterShard>
{
    private readonly List<ClusterShard> _clusterShards = Initialize(result);
    public IEnumerator<ClusterShard> GetEnumerator() => _clusterShards.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Count => _clusterShards.Count;

    private static List<ClusterShard> Initialize(ParseResult result)
    {
        var clusterShards = new List<ClusterShard>();
        
        if (result.TryToArray(out var resultArray))
        {
            foreach (var pResult in resultArray)
            {
                clusterShards.Add(new(pResult));
            }
        }
        
        return clusterShards;
    }
}