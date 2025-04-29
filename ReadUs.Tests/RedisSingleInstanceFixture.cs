using System;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Testcontainers.Redis;
using Xunit;

namespace ReadUs.Tests;

public class RedisSingleInstanceFixture : IAsyncLifetime
{
    public readonly RedisContainer SingleNode = CreateNode($"single-node-{Guid.NewGuid()}").Build();

    public async Task InitializeAsync() => await SingleNode.StartAsync();

    public async Task DisposeAsync()
    {
        await SingleNode.StopAsync();
        await SingleNode.DisposeAsync();
    }

    public Uri GetConnectionString() => new($"redis://{SingleNode.GetConnectionString()}");

    private static RedisBuilder CreateNode(string name) => new RedisBuilder()
        .WithName(name)
        .WithImage("redis:7.0")
        .WithPortBinding(61_379, 6379)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"));
}