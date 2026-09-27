using System.Globalization;
using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ReadUs.Tests.Integration.Fixtures;

/// <summary>
/// Four disposable Redis Cluster nodes per test run (project spec §9.2) — three
/// masters bootstrapped via <c>redis-cli --cluster create</c> and a fourth made a real
/// replica of node 1 via <c>CLUSTER MEET</c>/<c>CLUSTER REPLICATE</c>, exactly the
/// topology the session's original manually-run fixture ended up with (see design doc
/// §3.2's "Recorded during implementation" for why a replica was added at all).
///
/// Host networking, not bridge — Redis Cluster nodes gossip their own announced
/// address to each other and to any client that asks (<c>CLUSTER SHARDS</c>), so every
/// node's address must be the *same* one both other nodes and this out-of-Docker test
/// process can reach. Bridge networking's per-container random-port-mapping model has
/// no way to satisfy that without the containers announcing an address the host-side
/// test process can't route to. Host networking sidesteps the whole problem: a
/// container's ports *are* the host's ports, so a free host port found before the
/// container starts is unambiguously the one address everyone uses.
/// </summary>
public sealed class ClusterRedisFixture : IAsyncLifetime
{
    private readonly List<IContainer> _containers = [];

    public IReadOnlyList<EndPoint> SeedEndpoints { get; private set; } = [];

    /// <summary>The replica's endpoint (node 4 — replicates the master at <see cref="SeedEndpoints"/>[0], slots 0-5460).</summary>
    public EndPoint ReplicaEndpoint { get; private set; } = null!;

    /// <summary>
    /// The 4 managed containers in creation order — index 0-2 are the masters in the
    /// same order as <see cref="SeedEndpoints"/> (so index 2 owns slots 10923-16383),
    /// index 3 is the replica. Exposed so a test can stop/start a specific node
    /// natively, rather than needing to shell out to <c>docker</c> by a fixed name the
    /// way the manually-provisioned fixture this replaces had to.
    /// </summary>
    public IReadOnlyList<IContainer> Containers => _containers;

    public async Task InitializeAsync()
    {
        int[] ports = [.. Enumerable.Range(0, 4).Select(_ => FreePort.Find())];

        foreach (var port in ports)
        {
            var container = new ContainerBuilder("redis:8.10.1")
                .WithCreateParameterModifier(parameters => parameters.HostConfig!.NetworkMode = "host")
                .WithCommand(
                    "redis-server",
                    "--port", port.ToString(CultureInfo.InvariantCulture),
                    "--cluster-enabled", "yes",
                    "--cluster-config-file", $"nodes-{port}.conf",
                    "--cluster-node-timeout", "5000",
                    "--appendonly", "no",
                    "--daemonize", "no",
                    "--cluster-announce-ip", "127.0.0.1",
                    "--cluster-announce-port", port.ToString(CultureInfo.InvariantCulture),
                    "--cluster-announce-bus-port", (port + 10000).ToString(CultureInfo.InvariantCulture),
                    "--save")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(port))
                .Build();

            await container.StartAsync().ConfigureAwait(false);
            _containers.Add(container);
        }

        var masterPorts = ports[..3];
        var replicaPort = ports[3];
        var bootstrap = _containers[0];

        var createArgs = new List<string> { "redis-cli", "--cluster", "create" };
        createArgs.AddRange(masterPorts.Select(p => $"127.0.0.1:{p.ToString(CultureInfo.InvariantCulture)}"));
        createArgs.Add("--cluster-yes");
        await ExecOrThrowAsync(bootstrap, createArgs).ConfigureAwait(false);

        await ExecOrThrowAsync(bootstrap, ["redis-cli", "-p", masterPorts[0].ToString(CultureInfo.InvariantCulture), "CLUSTER", "MEET", "127.0.0.1", replicaPort.ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);

        var node1Id = (await ExecOrThrowAsync(bootstrap, ["redis-cli", "-p", masterPorts[0].ToString(CultureInfo.InvariantCulture), "CLUSTER", "MYID"]).ConfigureAwait(false)).Trim();

        // The MEET above needs a moment to propagate before this node accepts REPLICATE.
        await WaitUntilAsync(
            async () =>
            {
                var nodes = await ExecOrThrowAsync(bootstrap, ["redis-cli", "-p", replicaPort.ToString(CultureInfo.InvariantCulture), "CLUSTER", "NODES"]).ConfigureAwait(false);
                return nodes.Contains(node1Id, StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        await ExecOrThrowAsync(_containers[3], ["redis-cli", "-p", replicaPort.ToString(CultureInfo.InvariantCulture), "CLUSTER", "REPLICATE", node1Id]).ConfigureAwait(false);

        await WaitUntilAsync(
            async () =>
            {
                var info = await ExecOrThrowAsync(bootstrap, ["redis-cli", "-p", masterPorts[0].ToString(CultureInfo.InvariantCulture), "CLUSTER", "INFO"]).ConfigureAwait(false);
                return info.Contains("cluster_state:ok", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        await WaitUntilAsync(
            async () =>
            {
                var replication = await ExecOrThrowAsync(_containers[3], ["redis-cli", "-p", replicaPort.ToString(CultureInfo.InvariantCulture), "INFO", "replication"]).ConfigureAwait(false);
                return replication.Contains("master_link_status:up", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        SeedEndpoints = [.. masterPorts.Select(p => (EndPoint)new DnsEndPoint("127.0.0.1", p))];
        ReplicaEndpoint = new DnsEndPoint("127.0.0.1", replicaPort);
    }

    public async Task DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ExecOrThrowAsync(IContainer container, IList<string> command)
    {
        var result = await container.ExecAsync(command).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"`{string.Join(' ', command)}` failed (exit {result.ExitCode}): {result.Stderr}");
        }

        return result.Stdout;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for the cluster fixture to converge.");
    }

}

[CollectionDefinition(Name)]
public sealed class ClusterRedisCollection : ICollectionFixture<ClusterRedisFixture>
{
    public const string Name = "ClusterRedis";
}

/// <summary>
/// For the one test class that genuinely needs both a standalone server and a cluster
/// at once (<c>BatchExecutionTests</c>) — its own collection, since a class belongs to
/// exactly one xUnit collection and <see cref="ClusterRedisCollection"/>/
/// <see cref="StandaloneRedisCollection"/> are each already shared by other classes.
/// Costs one extra standalone container for the whole run; not worth avoiding by
/// merging it into either of those.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ClusterAndStandaloneRedisCollection : ICollectionFixture<ClusterRedisFixture>, ICollectionFixture<StandaloneRedisFixture>
{
    public const string Name = "ClusterAndStandaloneRedis";
}
