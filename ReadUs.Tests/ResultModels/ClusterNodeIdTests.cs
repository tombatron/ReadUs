using JetBrains.Annotations;
using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.ResultModels;

[UsedImplicitly]
public class ClusterNodeIdTests
{
    public class ItCanImplicitlyCastTo
    {
        [Fact]
        public void ClusterNodeIdFromCharArray()
        {
            var expectedClusterNodeId = new ClusterNodeId("Hello World".ToCharArray());

            var castClusterNodeId = (ClusterNodeId)"Hello World".ToCharArray();

            Assert.Equal(expectedClusterNodeId, castClusterNodeId);
        }

        [Fact]
        public void StringFromClusterNodeId()
        {
            var testClusterNodeId = new ClusterNodeId("Good night".ToCharArray());

            var stringClusterNodeId = (string)testClusterNodeId;

            Assert.Equal("Good night", stringClusterNodeId);
        }
    }
}