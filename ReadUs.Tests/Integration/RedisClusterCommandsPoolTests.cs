using System;
using Xunit;

namespace ReadUs.Tests.Integration
{
    public class RedisClusterCommandsPoolTests
    {
        [Fact]
        public void CanConnectToCluster()
        {
            var connectionString = new Uri("redis://tombaserver.local:7000");

            RedisCommandsPool.TryGetClusterInformation(connectionString, out var nodes);

            var pool = new RedisClusterCommandsPool(nodes, 1);
        }
    }
}