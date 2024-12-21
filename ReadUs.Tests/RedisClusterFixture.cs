using ReadUs.ResultModels;
using System;
using static ReadUs.RedisClusterConnectionPool;

namespace ReadUs.Tests;

// ReSharper disable once ClassNeverInstantiated.Global
public class RedisClusterFixture : IDisposable
{
    // TODO: Testcontainers...
    public ClusterNodesResult ClusterNodes { get; }
    public RedisConnectionConfiguration Configuration { get; }

    public RedisClusterFixture()
    {
        var connectionString = new Uri("redis://tombaserver.local:6379");

        TryGetClusterInformation(connectionString, out var clusterNodes);

        ClusterNodes = clusterNodes;
        Configuration = connectionString;
    }

    public void Dispose()
    {
    }
}