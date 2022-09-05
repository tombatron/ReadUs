using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests
{
    public class RedisClusterConnectionTests : IClassFixture<RedisClusterFixture>
    {
        private readonly RedisClusterFixture _redisClusterFixture;

        public RedisClusterConnectionTests(RedisClusterFixture redisClusterFixture) =>
            _redisClusterFixture = redisClusterFixture;

        [Fact]
        public void ItInitializesAllNodes()
        {
            using var clusterConnection = new RedisClusterConnection(_redisClusterFixture.ClusterNodes);

            var detectedClusterNodes = _redisClusterFixture.ClusterNodes.Count();

            Assert.Equal(detectedClusterNodes, clusterConnection.Count);
        }

        [Fact]
        public void ItWillConnectToAllInitializedNodes()
        {
            using var clusterConnection = new RedisClusterConnection(_redisClusterFixture.ClusterNodes);

            // Count how many nodes are not connected.
            var unconnectedNodes = clusterConnection.Count(x => !x.IsConnected);

            // Call connect on the cluster connection (which remember, is just a collection of nodes).
            clusterConnection.Connect();

            // Count how many nodes are now connected.
            var connectedNodes = clusterConnection.Count(x => x.IsConnected);

            // The unconnected nodes and connected nodes count should now be equal.
            Assert.Equal(unconnectedNodes, connectedNodes);
        }

        [Fact]
        public async Task ItWillConnectToAllInitializedNodesAsync()
        {
            using var clusterConnection = new RedisClusterConnection(_redisClusterFixture.ClusterNodes);

            // Count how many nodes are not connected.
            var unconnectedNodes = clusterConnection.Count(x => !x.IsConnected);

            // Call connect on the cluster connection (which remember, is just a collection of nodes).
            await clusterConnection.ConnectAsync();

            // Count how many nodes are now connected.
            var connectedNodes = clusterConnection.Count(x => x.IsConnected);

            // The unconnected nodes and connected nodes count should now be equal.
            Assert.Equal(unconnectedNodes, connectedNodes);
        }

        [Fact]
        public void DisposingClusterConnectionWillDisconnectAllNodes()
        {
            using var clusterConnection = new RedisClusterConnection(_redisClusterFixture.ClusterNodes);

            clusterConnection.Connect();

            // Count how many nodes are connected.
            var connectedNodes = clusterConnection.Count(x => x.IsConnected);

            clusterConnection.Dispose();

            // Count how many disconnected nodes there are.
            var disconnectedNodes = clusterConnection.Count(x => !x.IsConnected);

            // The connected and disconnected counts should now equal.
            Assert.Equal(connectedNodes, disconnectedNodes);
        }
    }
}