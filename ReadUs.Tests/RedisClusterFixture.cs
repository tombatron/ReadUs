using ReadUs.ResultModels;
using System;
using static ReadUs.RedisClusterConnectionPool;

namespace ReadUs.Tests
{
    public class RedisClusterFixture : IDisposable
    {
        public ClusterNodesResult ClusterNodes { get; }
        public RedisConnectionConfiguration Configuration { get; }

        public RedisClusterFixture()
        {
            var connectionString = new Uri("redis://tombaserver.local:7000");

            TryGetClusterInformation(connectionString, out var clusterNodes);

            ClusterNodes = clusterNodes;
            Configuration = connectionString;
        }

        public void Dispose()
        {
        }
    }
}