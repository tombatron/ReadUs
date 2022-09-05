using ReadUs.ResultModels;
using System;
using static ReadUs.RedisConnectionPool;

namespace ReadUs.Tests
{
    public class RedisClusterFixture : IDisposable
    {
        public ClusterNodesResult ClusterNodes { get; }

        public RedisClusterFixture()
        {
            var connectionString = new Uri("redis://tombaserver.local:7000");

            TryGetClusterInformation(connectionString, out var clusterNodes);

            ClusterNodes = clusterNodes;
        }

        public void Dispose()
        {
        }
    }
}