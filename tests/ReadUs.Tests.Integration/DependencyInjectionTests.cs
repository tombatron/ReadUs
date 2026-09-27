using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReadUs.Connections;
using ReadUs.Extensions.DependencyInjection;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>Exercises the DI registration helpers (project spec §10) against a real, disposable Testcontainers-managed server (project spec §9.2), including disposal.</summary>
[Collection(StandaloneRedisCollection.Name)]
public class DependencyInjectionTests(StandaloneRedisFixture fixture)
{
    [Fact]
    public async Task RegisteredClientResolvesAndExecutesCommands()
    {
        var services = new ServiceCollection();
        services.AddReadUsClient(new RedisConnectionOptions { EndPoint = fixture.EndPoint });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<RedisClient>();

        var result = await client.ExecuteAsync("PING"u8.ToArray(), []);
        Assert.Equal("PONG", result.AsString());
    }

    [Fact]
    public async Task ResolvingTwiceReturnsTheSameSingletonInstance()
    {
        var services = new ServiceCollection();
        services.AddReadUsClient(new RedisConnectionOptions { EndPoint = fixture.EndPoint });

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<RedisClient>();
        var second = provider.GetRequiredService<RedisClient>();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task DisposingTheProviderDisposesTheClient()
    {
        var services = new ServiceCollection();
        services.AddReadUsClient(new RedisConnectionOptions { EndPoint = fixture.EndPoint });

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<RedisClient>();
        await client.ExecuteAsync("PING"u8.ToArray(), []);

        await provider.DisposeAsync();

        // The pool's connections are now closed; using the client afterward must fail
        // clearly rather than silently succeed against a torn-down client.
        await Assert.ThrowsAsync<RedisConnectionException>(async () =>
            await client.ExecuteAsync("PING"u8.ToArray(), []));
    }

    [Fact]
    public async Task OptionsFactoryOverloadReceivesTheServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(fixture.EndPoint.Host);
        services.AddReadUsClient(sp => new RedisConnectionOptions { EndPoint = new DnsEndPoint(sp.GetRequiredService<string>(), fixture.EndPoint.Port) });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<RedisClient>();

        var result = await client.ExecuteAsync("PING"u8.ToArray(), []);
        Assert.Equal("PONG", result.AsString());
    }
}
