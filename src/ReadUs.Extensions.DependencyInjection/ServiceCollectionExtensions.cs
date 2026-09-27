using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReadUs.Cluster;
using ReadUs.Connections;
using ReadUs.Sentinel;

namespace ReadUs.Extensions.DependencyInjection;

/// <summary>
/// <c>Microsoft.Extensions.DependencyInjection</c> registration helpers (project spec
/// §10). Each client is registered as a singleton — a <see cref="RedisClient"/>,
/// <see cref="ClusterClient"/>, or <see cref="SentinelClient"/> owns a pool of physical
/// connections meant to live for the application's lifetime, not be created per
/// request.
///
/// .NET's built-in container has no first-class async factory support, and building a
/// client is inherently async (it connects and completes a RESP handshake before it's
/// usable). Rather than force every caller to route around that, these helpers block
/// the resolving thread once, the first time the client is resolved — the same
/// trade-off most .NET libraries in this position make (registering a connected client
/// synchronously, e.g. <c>ConnectionMultiplexer.Connect</c>). If that block is
/// unacceptable at startup (e.g. inside a request path rather than app initialization),
/// resolve the client eagerly during host startup instead of lazily on first request.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddReadUsClient(
        this IServiceCollection services,
        RedisConnectionOptions options,
        int connectionCount = 4,
        int leasedConnectionCount = 4) =>
        services.AddSingleton(_ => RedisClient.ConnectAsync(options, connectionCount, leasedConnectionCount).GetAwaiter().GetResult());

    public static IServiceCollection AddReadUsClient(
        this IServiceCollection services,
        Func<IServiceProvider, RedisConnectionOptions> optionsFactory,
        int connectionCount = 4,
        int leasedConnectionCount = 4) =>
        services.AddSingleton(sp => RedisClient.ConnectAsync(optionsFactory(sp), connectionCount, leasedConnectionCount).GetAwaiter().GetResult());

    public static IServiceCollection AddReadUsClusterClient(
        this IServiceCollection services,
        IReadOnlyList<EndPoint> seedEndpoints,
        Func<EndPoint, RedisConnectionOptions>? optionsFactory = null) =>
        services.AddSingleton(_ => ClusterClient.ConnectAsync(seedEndpoints, optionsFactory).GetAwaiter().GetResult());

    public static IServiceCollection AddReadUsSentinelClient(
        this IServiceCollection services,
        IReadOnlyList<EndPoint> sentinelEndpoints,
        string serviceName,
        Func<EndPoint, RedisConnectionOptions>? masterOptionsFactory = null) =>
        services.AddSingleton(_ => SentinelClient.ConnectAsync(sentinelEndpoints, serviceName, masterOptionsFactory).GetAwaiter().GetResult());
}
