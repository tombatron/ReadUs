using System.Globalization;
using System.Net;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ReadUs.Tests.Integration.Fixtures;

/// <summary>
/// A disposable Sentinel constellation per test run (project spec §9.2): a master, a
/// replica, and three sentinels monitoring service name <c>mymaster</c> — the same
/// topology the session's original manually-run fixture used. Host networking, for the
/// same reason as <see cref="ClusterRedisFixture"/>: Sentinel announces addresses (the
/// master's, and its own to peer sentinels) that both the other containers and this
/// out-of-Docker test process must resolve to the same place.
/// </summary>
public sealed class SentinelRedisFixture : IAsyncLifetime
{
    private const string ServiceName = "mymaster";

    private readonly List<IContainer> _containers = [];

    public IReadOnlyList<EndPoint> SentinelEndpoints { get; private set; } = [];

    public async Task InitializeAsync()
    {
        var masterPort = FreePort.Find();
        var replicaPort = FreePort.Find();
        int[] sentinelPorts = [.. Enumerable.Range(0, 3).Select(_ => FreePort.Find())];

        var master = BuildRedisContainer(masterPort, "redis-server", "--port", masterPort.ToString(CultureInfo.InvariantCulture), "--save");
        await master.StartAsync().ConfigureAwait(false);
        _containers.Add(master);

        var replica = BuildRedisContainer(
            replicaPort,
            "redis-server", "--port", replicaPort.ToString(CultureInfo.InvariantCulture),
            "--replicaof", "127.0.0.1", masterPort.ToString(CultureInfo.InvariantCulture),
            "--save");
        await replica.StartAsync().ConfigureAwait(false);
        _containers.Add(replica);

        await WaitUntilAsync(
            async () =>
            {
                var info = await ExecOrThrowAsync(replica, ["redis-cli", "-p", replicaPort.ToString(CultureInfo.InvariantCulture), "INFO", "replication"]).ConfigureAwait(false);
                return info.Contains("master_link_status:up", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        var sentinels = new List<IContainer>();
        foreach (var sentinelPort in sentinelPorts)
        {
            var config = string.Join(
                '\n',
                $"port {sentinelPort.ToString(CultureInfo.InvariantCulture)}",
                $"sentinel monitor {ServiceName} 127.0.0.1 {masterPort.ToString(CultureInfo.InvariantCulture)} 2",
                "sentinel down-after-milliseconds mymaster 3000",
                "sentinel failover-timeout mymaster 10000",
                "sentinel parallel-syncs mymaster 1");

            // redis-sentinel rewrites its own config file to persist discovered state
            // (peer sentinels, replicas), so it must actually be writable to the process
            // running it — WithResourceMapping's copied file ended up owned by a user
            // the entrypoint's own privilege-drop-to-"redis" left unable to write it,
            // even with permissive mode bits. Writing the file via a root shell command
            // at startup instead of a resource mapping sidesteps the ownership question
            // entirely; overriding the entrypoint means the image's own "redis-server"/
            // "redis-sentinel" argument-sniffing (which is what drops to the "redis"
            // user) never runs, so this whole command chain, including the eventual
            // redis-sentinel process, simply stays root — harmless for a throwaway test
            // container.
            var configBase64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(config));
            var sentinel = new ContainerBuilder("redis:8.10.1")
                .WithCreateParameterModifier(parameters => parameters.HostConfig!.NetworkMode = "host")
                .WithEntrypoint("sh", "-c")
                .WithCommand($"echo {configBase64} | base64 -d > /etc/sentinel.conf && exec redis-sentinel /etc/sentinel.conf")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(sentinelPort))
                .Build();

            await sentinel.StartAsync().ConfigureAwait(false);
            sentinels.Add(sentinel);
            _containers.Add(sentinel);
        }

        // Each sentinel needs a moment to actually see the master it was configured to
        // monitor before this fixture hands out endpoints a test might immediately use.
        foreach (var (sentinel, port) in sentinels.Zip(sentinelPorts))
        {
            await WaitUntilAsync(
                async () =>
                {
                    var masters = await ExecOrThrowAsync(sentinel, ["redis-cli", "-p", port.ToString(CultureInfo.InvariantCulture), "SENTINEL", "MASTERS"]).ConfigureAwait(false);
                    return masters.Contains(ServiceName, StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }

        SentinelEndpoints = [.. sentinelPorts.Select(p => (EndPoint)new DnsEndPoint("127.0.0.1", p))];
    }

    public async Task DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IContainer BuildRedisContainer(int port, params string[] command) =>
        new ContainerBuilder("redis:8.10.1")
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.NetworkMode = "host")
            .WithCommand(command)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(port))
            .Build();

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

        throw new TimeoutException("Timed out waiting for the sentinel fixture to converge.");
    }
}

[CollectionDefinition(Name)]
public sealed class SentinelRedisCollection : ICollectionFixture<SentinelRedisFixture>
{
    public const string Name = "SentinelRedis";
}
