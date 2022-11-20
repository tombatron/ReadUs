using System;

namespace ReadUs.Tests
{
    internal static class TestUtilities
    {
        internal static RedisConnectionConfiguration SingleInstanceConnectionConfigurtion
        {
            get
            {
                var address = Environment.GetEnvironmentVariable("Single_Instance_Redis_Connection_String") ?? "redis://tombaserver.local:6379";

                return new Uri(address);
            }
        }

        internal static RedisConnectionConfiguration ClusterConnectionConfiguration
        {
            get
            {
                var address = Environment.GetEnvironmentVariable("Cluster_Redis_Connection_String") ?? "redis://tombaserver.local:7000?connectionsPerNode=5";

                return new Uri(address);
            }
        }
    }
}