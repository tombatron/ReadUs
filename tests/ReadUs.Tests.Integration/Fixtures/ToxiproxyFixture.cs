using System.Net;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace ReadUs.Tests.Integration.Fixtures;

/// <summary>
/// A disposable Redis instance sitting behind a disposable Toxiproxy instance
/// (project spec §9.2: "Inject: mid-response TCP resets, partial writes/reads (via a
/// proxy layer like Toxiproxy or a custom byte-delaying stream wrapper)... " — this
/// fixture is the Toxiproxy half). Both containers join a private Docker network so
/// Toxiproxy can reach Redis by container name; the .NET test process (outside Docker)
/// only ever talks to Toxiproxy's own mapped ports, never to Redis directly, so every
/// fault a test injects genuinely sits on the wire between ReadUs and the server.
///
/// One shared proxy for the whole fixture, not one per test: a Toxiproxy proxy is
/// cheap to create once and reuse, and its listen port has to be a container port
/// mapped *before* the container starts, which rules out creating a fresh proxy (on a
/// fresh port) per test without restarting the container. Tests add and remove their
/// own named toxics against this one proxy instead — collection tests run
/// sequentially, so there's no cross-test toxic interference.
/// </summary>
#pragma warning disable CA1001 // _control is disposed in DisposeAsync (IAsyncLifetime), which the analyzer doesn't recognize as satisfying this — there's no synchronous IDisposable on this type to add instead.
public sealed class ToxiproxyFixture : IAsyncLifetime
#pragma warning restore CA1001
{
    private const int ProxyContainerPort = 8666;
    private const string ProxyName = "redis";

    private INetwork? _network;
    private IContainer? _redisContainer;
    private IContainer? _toxiproxyContainer;
    private HttpClient? _control;

    /// <summary>Where ReadUs should connect — Toxiproxy's proxy listener, not the real Redis server.</summary>
    public DnsEndPoint ProxiedEndPoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync().ConfigureAwait(false);

        _redisContainer = new ContainerBuilder("redis:8.10.1")
            .WithNetwork(_network)
            .WithNetworkAliases("redis-target")
            .WithCommand("redis-server", "--port", "6379", "--save")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        await _redisContainer.StartAsync().ConfigureAwait(false);

        // The official image is scratch-based with no shell at all (no `sh`, no
        // `cat`) — an exec-based wait strategy like UntilInternalTcpPortIsAvailable
        // can never succeed against it (it retries forever rather than failing
        // loudly). An HTTP-based check, performed from outside the container against
        // the mapped control port, needs nothing inside the container at all.
        _toxiproxyContainer = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.9.0")
            .WithNetwork(_network)
            .WithPortBinding(8474, assignRandomHostPort: true)
            .WithPortBinding(ProxyContainerPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPath("/version").ForPort(8474)))
            .Build();
        await _toxiproxyContainer.StartAsync().ConfigureAwait(false);

        _control = new HttpClient
        {
            BaseAddress = new Uri($"http://{_toxiproxyContainer.Hostname}:{_toxiproxyContainer.GetMappedPublicPort(8474)}"),
        };

        var createResponse = await _control.PostAsJsonAsync(
            "/proxies",
            new { name = ProxyName, listen = $"0.0.0.0:{ProxyContainerPort}", upstream = "redis-target:6379" }).ConfigureAwait(false);
        createResponse.EnsureSuccessStatusCode();

        ProxiedEndPoint = new DnsEndPoint(_toxiproxyContainer.Hostname, _toxiproxyContainer.GetMappedPublicPort(ProxyContainerPort));
    }

    /// <summary><paramref name="attributes"/> is serialized as-is as the toxic's JSON "attributes" object — shape depends on <paramref name="type"/> (Toxiproxy's own toxic types: "reset_peer", "slicer", "latency", ...).</summary>
    public async Task AddToxicAsync(string toxicName, string type, string stream, object attributes)
    {
        var response = await _control!.PostAsJsonAsync(
            $"/proxies/{ProxyName}/toxics",
            new { name = toxicName, type, stream, attributes }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveToxicAsync(string toxicName)
    {
        using var response = await _control!.DeleteAsync($"/proxies/{ProxyName}/toxics/{toxicName}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        _control?.Dispose();

        if (_toxiproxyContainer is not null)
        {
            await _toxiproxyContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (_network is not null)
        {
            await _network.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ToxiproxyCollection : ICollectionFixture<ToxiproxyFixture>
{
    public const string Name = "Toxiproxy";
}
