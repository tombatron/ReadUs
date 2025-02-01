using System.Text;
using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.ResultModels;

public class ClusterNodesResultTests
{
    private const string SampleRawClusterNodesResult =
        "$775\r\n65d8df12f4515df293cbdf8d5014dc6273621bfc 192.168.86.40:7001@17001 master - 0 1659525976727 19 connected 5461-10922\n5953f8065390a9c6bb194762c21920e486f88252 192.168.86.40:7002@17002 master - 0 1659525976000 20 connected 10923-16383\n64f88596fec6e244d4f87fa5b702654b36848c35 192.168.86.40:7005@17005 slave 361a0b693ee878c23d0a45c16f965c15ea1e37e6 0 1659525977230 17 connected\ne7ed94e397b04f681b8993bb501867a511dbdaf4 192.168.86.40:7004@17004 slave 5953f8065390a9c6bb194762c21920e486f88252 0 1659525978235 20 connected\n89c1e6c03dc9fe0a2227f55ff4dcb383350196d2 192.168.86.40:7003@17003 slave 65d8df12f4515df293cbdf8d5014dc6273621bfc 0 1659525977000 19 connected\n361a0b693ee878c23d0a45c16f965c15ea1e37e6 192.168.86.40:7000@17000 myself,master - 0 1659525977000 17 connected 0-5460\n\r\n";

    [Fact]
    public void YieldsCorrectNumberOfClusterNodeEntries()
    {
        var rawBytes = Encoding.UTF8.GetBytes(SampleRawClusterNodesResult);

        // var parsedResult = Parse(rawBytes);

        var clusterNodeData = new ClusterNodesResult(rawBytes);

        Assert.Equal(6, clusterNodeData.ToArray().Length);
    }

    [Fact]
    public void ItCanCreateASignature()
    {
        var rawBytes = Encoding.UTF8.GetBytes(SampleRawClusterNodesResult);

        // var parsedResult = Parse(rawBytes);

        var clusterNodeData = new ClusterNodesResult(rawBytes);

        Assert.Equal("66840ECA86CEA3172C031694A1F1787B", clusterNodeData.GetNodesSignature());
    }

    public class OverridenToString
    {
        [Fact]
        public void YieldsExpectedResult()
        {
            var rawBytes = Encoding.UTF8.GetBytes(SampleRawClusterNodesResult);

            //var parsedResult = Parse(rawBytes);

            var clusterNodeData = new ClusterNodesResult(rawBytes);

            Assert.Equal(
                "65d8df12f4515df293cbdf8d5014dc6273621bfc:192.168.86.40:7001@17001:master:5461,10922|5953f8065390a9c6bb194762c21920e486f88252:192.168.86.40:7002@17002:master:10923,16383|64f88596fec6e244d4f87fa5b702654b36848c35:192.168.86.40:7005@17005:slave:NOSLOTS|e7ed94e397b04f681b8993bb501867a511dbdaf4:192.168.86.40:7004@17004:slave:NOSLOTS|89c1e6c03dc9fe0a2227f55ff4dcb383350196d2:192.168.86.40:7003@17003:slave:NOSLOTS|361a0b693ee878c23d0a45c16f965c15ea1e37e6:192.168.86.40:7000@17000:myself|master:0,5460",
                clusterNodeData.ToString());
        }
    }
}