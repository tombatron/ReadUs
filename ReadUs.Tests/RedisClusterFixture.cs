using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using ReadUs.ResultModels;
using Testcontainers.Redis;
using Xunit;
using static ReadUs.RedisClusterConnectionPool;

namespace ReadUs.Tests;

// ReSharper disable once ClassNeverInstantiated.Global
public class RedisClusterFixture : IAsyncLifetime
{
    private static readonly ConcurrentStack<int> Ports = new(Enumerable.Range(7_000, 5_000));


    public static readonly INetwork ClusterNetwork = new NetworkBuilder()
        .WithName("cluster-network")
        .WithDriver(NetworkDriver.Bridge)
        .Build();

    private readonly List<(string name, int port)> _containers = new();

    public readonly RedisContainer Node1;
    public readonly RedisContainer Node2;
    public readonly RedisContainer Node3;
    public readonly RedisContainer Node4;
    public readonly RedisContainer Node5;
    public readonly RedisContainer Node6;

    public RedisClusterFixture()
    {
        Console.WriteLine("Creating Redis Cluster Fixture");

        Node1 = CreateNode("node-1").Build();
        Node2 = CreateNode("node-2").Build();
        Node3 = CreateNode("node-3").Build();
        Node4 = CreateNode("node-4").Build();
        Node5 = CreateNode("node-5").Build();
        Node6 = CreateNode("node-6").Build();
    }

    public ClusterNodesResult ClusterNodes { get; private set; }
    public RedisConnectionConfiguration Configuration { get; private set; }

    public async Task InitializeAsync()
    {
        StartTesting();

        await ClusterNetwork.CreateAsync();

        await Node1.StartAsync();
        await Node2.StartAsync();
        await Node3.StartAsync();
        await Node4.StartAsync();
        await Node5.StartAsync();
        await Node6.StartAsync();

        var clusterCreationResult = await Node1.ExecAsync(GetClusterCreationCommand(1));

        var output = clusterCreationResult.Stdout;

        Console.WriteLine(output);

        var err = clusterCreationResult.Stderr;

        // If the cluster was created correctly, we expect that there will be nothing present in the standard error
        // output, and that the end of the standard output will be that all 16,384 slots are covered by the cluster.
        Assert.Empty(err);
        Assert.EndsWith("[OK] All 16384 slots covered.\n", output);


        await Task.Delay(1_000); // Wait a second for Redis to settle down and have properly assigned node roles.

        var connectionString = new Uri($"redis://localhost:{_containers[0].port}");

        TryGetClusterInformation(connectionString, out var clusterNodes);

        ClusterNodes = clusterNodes;
        Configuration = connectionString;
    }

    public async Task DisposeAsync()
    {
        try
        {
            await Node6.StopAsync();
            await Node5.StopAsync();
            await Node4.StopAsync();
            await Node3.StopAsync();
            await Node2.StopAsync();
            await Node1.StopAsync();

            await ClusterNetwork.DeleteAsync();

            await Node6.DisposeAsync();
            await Node5.DisposeAsync();
            await Node4.DisposeAsync();
            await Node3.DisposeAsync();
            await Node2.DisposeAsync();
            await Node1.DisposeAsync();

            await ClusterNetwork.DisposeAsync();

            StopTesting();
        }
        catch
        {
            // ¯\_(ツ)_/¯
        }
    }

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
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "-p", serverPort, "PING"));
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

        foreach (var (container, port) in _containers) commandResult.Add($"{container}:{port}");

        commandResult.Add("--cluster-replicas");
        commandResult.Add(numberOfReplicas.ToString());

        return commandResult;
    }
}