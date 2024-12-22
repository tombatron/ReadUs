using ReadUs.ResultModels;
using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using Testcontainers.Redis;
using Xunit;
using static ReadUs.RedisClusterConnectionPool;

namespace ReadUs.Tests;

// ReSharper disable once ClassNeverInstantiated.Global
public class RedisClusterFixture : IAsyncLifetime
{
    public ClusterNodesResult ClusterNodes { get; private set; }
    public RedisConnectionConfiguration Configuration { get; private set; }
    
    
    public static readonly INetwork ClusterNetwork = new NetworkBuilder()
        .WithName("cluster-network")
        .WithDriver(NetworkDriver.Bridge)
        .Build();

    public readonly RedisContainer Node1 = new RedisBuilder()
        .WithImage("redis:7.0")
        .WithName("node-1")
        .WithCommand(
            "redis-server",
            "--port", "6379",
            "--cluster-announce-port", "6379",
            "--cluster-announce-bus-port", "16379",          
            "--cluster-enabled", "yes",
            "--cluster-config-file", "nodes.conf",
            "--cluster-node-timeout", "5000",
            "--bind", "0.0.0.0",
            "--appendonly", "no",
            "--save", ""
        )
        .WithNetwork(ClusterNetwork)
        .WithHostname("node-1")
        .WithExposedPort(6379)        
        .WithPortBinding(6379, 6379)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"))
        .Build();
    
    public readonly RedisContainer Node2 = new RedisBuilder()
        .WithImage("redis:7.0")
        .WithName("node-2")
        .WithCommand(
            "redis-server",
            "--port", "6379",
            "--cluster-announce-port", "6379",
            "--cluster-announce-bus-port", "16379",
            "--cluster-enabled", "yes",
            "--cluster-config-file", "nodes.conf",
            "--cluster-node-timeout", "5000",
            "--bind", "0.0.0.0",
            "--appendonly", "no",
            "--save", ""
        )
        .WithNetwork(ClusterNetwork)
        .WithHostname("node-2")
        .WithExposedPort(6379)
        .WithPortBinding(6380, 6379)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"))
        .Build();
    
    public readonly RedisContainer Node3 = new RedisBuilder()
        .WithImage("redis:7.0")
        .WithName("node-3")
        .WithCommand(
            "redis-server",
            "--port", "6379",
            "--cluster-announce-port", "6379",
            "--cluster-announce-bus-port", "16379",
            "--cluster-enabled", "yes",
            "--cluster-config-file", "nodes.conf",
            "--cluster-node-timeout", "5000",
            "--bind", "0.0.0.0",
            "--appendonly", "no",
            "--save", ""
        )
        .WithNetwork(ClusterNetwork)
        .WithHostname("node-3")
        .WithExposedPort(6379)        
        .WithPortBinding(6381, 6379)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"))
        .Build();       
    
    public async Task InitializeAsync()
    {
        StartTesting();
        
        await ClusterNetwork.CreateAsync();
        
        await Node1.StartAsync();
        await Node2.StartAsync();
        await Node3.StartAsync();

        var clusterCreationResult = await Node1.ExecAsync([
            "redis-cli",
            "--cluster-yes",
            "--cluster", "create", "node-1:6379", "node-2:6379", "node-3:6379",
            "--cluster-replicas", "0"
        ]);
        
        var output = clusterCreationResult.Stdout;
        var err = clusterCreationResult.Stderr;

        // If the cluster was created correctly, we expect that there will be nothing present in the standard error
        // output, and that the end of the standard output will be that all 16,384 slots are covered by the cluster.
        Assert.Empty(err);
        Assert.EndsWith("[OK] All 16384 slots covered.\n", output);
        
        var connectionString = new Uri($"redis://{Node1.GetConnectionString()}");
        
        TryGetClusterInformation(connectionString, out var clusterNodes);
        
        ClusterNodes = clusterNodes;
        Configuration = connectionString;
    }

    public async Task DisposeAsync()
    {
        await Node3.StopAsync();
        await Node2.StopAsync();
        await Node1.StopAsync();
        
        await ClusterNetwork.DeleteAsync();
        
        await Node3.DisposeAsync();
        await Node2.DisposeAsync();
        await Node1.DisposeAsync();
        
        await ClusterNetwork.DisposeAsync();

        StopTesting();
    }
}