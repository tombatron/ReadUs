using System;
using Xunit;
using static ReadUs.RedisClusterConnectionPool;

namespace ReadUs.Tests.Integration
{
    public class RedisClusterCommandsPoolTests
    {
        [Fact]
        public void CanConnectToCluster()
        {
            var connectionString = new Uri("redis://tombaserver.local:7000");

            TryGetClusterInformation(connectionString, out var nodes);

            var pool = new RedisClusterConnectionPool(nodes, connectionString);
        }
    }
}