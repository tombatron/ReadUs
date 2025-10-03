using System.Linq;
using System.Text;
using JetBrains.Annotations;
using ReadUs.ResultModels;
using Xunit;
using static ReadUs.Parser.Parser;

namespace ReadUs.Tests.ResultModels;

[UsedImplicitly]
public class ClusterShardsResultTests
{
    private const string SampleRawClusterSlotsResult =
        "*3\r\n*4\r\n$5\r\nslots\r\n*2\r\n:5461\r\n:10922\r\n$5\r\nnodes\r\n*2\r\n*14\r\n$2\r\nid\r\n$40\r\n10b5ef13c12f97eb088af5b17423af4ec1657e60\r\n$4\r\nport\r\n:6379\r\n$2\r\nip\r\n$10\r\n172.20.0.3\r\n$8\r\nendpoint\r\n$10\r\n172.20.0.3\r\n$4\r\nrole\r\n$6\r\nmaster\r\n$18\r\nreplication-offset\r\n:483135\r\n$6\r\nhealth\r\n$6\r\nonline\r\n*14\r\n$2\r\nid\r\n$40\r\ne06fbf2147b0634a21af14aceb11dcc134efa4d0\r\n$4\r\nport\r\n:6379\r\n$2\r\nip\r\n$10\r\n172.20.0.7\r\n$8\r\nendpoint\r\n$10\r\n172.20.0.7\r\n$4\r\nrole\r\n$7\r\nreplica\r\n$18\r\nreplication-offset\r\n:483135\r\n$6\r\nhealth\r\n$6\r\nonline\r\n*4\r\n$5\r\nslots\r\n*2\r\n:10923\r\n:16383\r\n$5\r\nnodes\r\n*2\r\n*14\r\n$2\r\nid\r\n$40\r\n9871ccdcaac82fbcc7a91c67638faa905b35b4ca\r\n$4\r\nport\r\n:6379\r\n$2\r\nip\r\n$10\r\n172.20.0.4\r\n$8\r\nendpoint\r\n$10\r\n172.20.0.4\r\n$4\r\nrole\r\n$6\r\nmaster\r\n$18\r\nreplication-offset\r\n:483317\r\n$6\r\nhealth\r\n$6\r\nonline\r\n*14\r\n$2\r\nid\r\n$40\r\na4934263fb1218b1792d0fff231dfb9e40405a74\r\n$4\r\nport\r\n:6379\r\n$2\r\nip\r\n$10\r\n172.20.0.5\r\n$8\r\nendpoint\r\n$10\r\n172.20.0.5\r\n$4\r\nrole\r\n$7\r\nreplica\r\n$18\r\nreplication-offset\r\n:483317\r\n$6\r\nhealth\r\n$6\r\nonline\r\n*4\r\n$5\r\nslots\r\n*2\r\n:0\r\n:5460\r\n$5\r\nnodes\r\n*2\r\n*14\r\n$2\r\nid\r\n$40\r\n175c30488e332e4fba367e3163d8ce585bed53e2\r\n$4\r\nport\r\n:6379\r\n$2\r\nip\r\n$10\r\n172.20.0.2\r\n$8\r\nendpoint\r\n$10\r\n172.20.0.2\r\n$4\r\nrole\r\n$7\r\nreplica\r\n$18\r\nreplication-offset\r\n:483174\r\n$6\r\nhealth\r\n$6\r\nonline\r\n*14\r\n$2\r\nid\r\n$40\r\n8d8187d13688da720715f7acf7fdeb70d64ea7bf\r\n$4\r\nport\r\n:6379\r\n$2\r\nip\r\n$10\r\n172.20.0.6\r\n$8\r\nendpoint\r\n$10\r\n172.20.0.6\r\n$4\r\nrole\r\n$6\r\nmaster\r\n$18\r\nreplication-offset\r\n:483174\r\n$6\r\nhealth\r\n$6\r\nonline\r\n";

    private readonly byte[] _rawBytes = Encoding.UTF8.GetBytes(SampleRawClusterSlotsResult);

    [Fact]
    public void YieldsCorrectNumberOfClusterGroups()
    {
        var parsedResponse = Parse(_rawBytes);
        var clusterSlotsResult = new ClusterShardsResult(parsedResponse.Unwrap());

        Assert.Equal(3, clusterSlotsResult.Count);
    }


    [Fact]
    public void ParsesSlotRangesCorrectly()
    {
        var parsed = Parse(_rawBytes);
        var result = new ClusterShardsResult(parsed.Unwrap());

        var expectedRanges = new[]
        {
            (5461, 10922),
            (10923, 16383),
            (0, 5460)
        };

        for (int i = 0; i < result.Count; i++)
        {
            var shard = result.ElementAt(i);
            Assert.NotNull(shard.Slots);
            Assert.Single(shard.Slots); // Each shard has one slot range

            var slot = shard.Slots[0];
            Assert.Equal(expectedRanges[i].Item1, slot.Begin);
            Assert.Equal(expectedRanges[i].Item2, slot.End);
        }
    }

    [Fact]
    public void ParsesNodesCorrectly()
    {
        var parsed = Parse(_rawBytes);
        var result = new ClusterShardsResult(parsed.Unwrap());

        foreach (var shard in result)
        {
            Assert.NotNull(shard.Nodes);
            Assert.Equal(2, shard.Nodes.Length); // Each shard has 1 master + 1 replica

            var master = shard.Nodes.First(n => n.Role == "master");
            var replica = shard.Nodes.First(n => n.Role == "replica");

            Assert.NotNull(master);
            Assert.NotNull(replica);

            Assert.Equal("online", master.Health);
            Assert.Equal("online", replica.Health);
        }
    }

    [Fact]
    public void HandlesEmptyInputGracefully()
    {
        var emptyBytes = "*0\r\n"u8.ToArray();
        var parsed = Parse(emptyBytes);
        var result = new ClusterShardsResult(parsed.Unwrap());

        Assert.Empty(result);
    }

    [Fact]
    public void ValidatesReplicationOffsetIsParsed()
    {
        var parsed = Parse(_rawBytes);
        var result = new ClusterShardsResult(parsed.Unwrap());

        foreach (var shard in result)
        {
            foreach (var node in shard!.Nodes!)
            {
                Assert.True(node.ReplicationOffset > 0);
            }
        }
    }
}