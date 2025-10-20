using ReadUs.Parser;
using SlotRange = ReadUs.Commands.ResultModels.ClusterSlots.SlotRange;

namespace ReadUs.Commands.ResultModels;

public class ClusterShard
{
    public SlotRange[]? Slots { get; }
    public ClusterShardsNode[]? Nodes { get; }

    public ClusterShard(ParseResult result)
    {
        if (result.TryToArray(out var resultArray))
        {
            var currentIndex = 0;

            if (resultArray[currentIndex].ToString() == "slots")
            {
                if (resultArray[++currentIndex].TryToArray(out var slotsArray))
                {
                    var slots = new SlotRange[slotsArray.Length / 2];
                    
                    for (var i = 0; i < slotsArray.Length; i++)
                    {
                        var startRange = int.Parse(slotsArray[i].Value);
                        var endRange = int.Parse(slotsArray[++i].Value);

                        slots[i - 1] = SlotRange.Create(startRange, endRange);
                    }

                    Slots = slots;
                }
                
                if (resultArray[++currentIndex].ToString() == "nodes")
                {
                    if (resultArray[++currentIndex].TryToArray(out var nodesArray))
                    {
                        var nodes = new ClusterShardsNode[nodesArray.Length];

                        for (var i = 0; i < nodesArray.Length; i++)
                        {
                            nodes[i] = new(nodesArray[i]);
                        }

                        Nodes = nodes;
                    }
                }
            }
        }
    }
}