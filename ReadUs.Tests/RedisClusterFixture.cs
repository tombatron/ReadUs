using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using ReadUs.ResultModels;
using static ReadUs.RedisClusterConnectionPool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Testcontainers.Redis;
using Xunit;

namespace ReadUs.Tests;

// ReSharper disable once ClassNeverInstantiated.Global
public class RedisClusterFixture : IAsyncLifetime
{
    private static readonly ConcurrentStack<int> Ports = new ConcurrentStack<int>(Enumerable.Range(7_000, 5_000));

    public ClusterNodesResult ClusterNodes { get; private set; }
    public RedisConnectionConfiguration Configuration { get; private set; }


    public static readonly INetwork ClusterNetwork = new NetworkBuilder()
        .WithName("cluster-network")
        .WithDriver(NetworkDriver.Bridge)
        .Build();

    public readonly RedisContainer Node1;
    public readonly RedisContainer Node2;
    public readonly RedisContainer Node3;

    public RedisClusterFixture()
    {
        Console.WriteLine("Creating Redis Cluster Fixture");

        Node1 = CreateNode("node-1").Build();
        Node2 = CreateNode("node-2").Build();
        Node3 = CreateNode("node-3").Build();
    }

    public async Task InitializeAsync()
    {
        StartTesting();

        await ClusterNetwork.CreateAsync();

        await Node1.StartAsync();
        await Node2.StartAsync();
        await Node3.StartAsync();

        var clusterCreationResult = await Node1.ExecAsync(GetClusterCreationCommand(0));

        var output = clusterCreationResult.Stdout;
        var err = clusterCreationResult.Stderr;

        // If the cluster was created correctly, we expect that there will be nothing present in the standard error
        // output, and that the end of the standard output will be that all 16,384 slots are covered by the cluster.
        Assert.Empty(err);
        Assert.EndsWith("[OK] All 16384 slots covered.\n", output);

        var connectionString = new Uri($"redis://localhost:{_containers[0].port}");

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

    private List<(string name, int port)> _containers = new List<(string name, int port)>();

    private RedisBuilder CreateNode(string baseName)
    {
        Ports.TryPop(out var port);

        var uniqueId = Guid.NewGuid().ToString("N");
        var containerName = $"{baseName}-{uniqueId}";
        var serverPort = port.ToString();
        var clusterBusPort = (port + 10000).ToString();

        _containers.Add((containerName, port));

        return new RedisBuilder()
            .WithImage("redis:7.0")
            .WithName(containerName)
            .WithCommand(
                "redis-server",
                "--port", serverPort,
                "--cluster-announce-port", serverPort,
                "--cluster-announce-bus-port", clusterBusPort,
                "--cluster-enabled", "yes",
                "--cluster-config-file", "nodes.conf",
                "--cluster-node-timeout", "5000",
                "--bind", "0.0.0.0",
                "--appendonly", "no",
                "--save", "")
            .WithNetwork(ClusterNetwork)
            .WithHostname(containerName)
            .WithExposedPort(port)
            .WithPortBinding(port, port)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "-p", serverPort,"PING"));
    }

    private IList<string> GetClusterCreationCommand(int numberOfReplicas)
    {
        var commandResult = new List<string>(8 + _containers.Count)
        {
            "redis-cli",
            "-p", _containers[0].port.ToString(),
            "--cluster-yes",
            "--cluster", "create"
        };

        foreach(var (container, port) in _containers)
        {
            commandResult.Add($"{container}:{port}");
        }

        commandResult.Add("--cluster-replicas");
        commandResult.Add(numberOfReplicas.ToString());

        return commandResult;
    }
}