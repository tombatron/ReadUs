using Xunit;

namespace ReadUs.Tests.Integration
{
    public class RedisClusterCommandsPoolTests
    {
        [Fact]
        public void CanConnectToCluster()
        {
            var pool = new RedisClusterCommandsPool("tombaserver.local", 7_000, 1);
        }
    }
}