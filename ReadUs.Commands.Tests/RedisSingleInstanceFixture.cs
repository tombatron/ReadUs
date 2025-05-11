using DotNet.Testcontainers.Builders;
using Testcontainers.Redis;
using Xunit;

namespace ReadUs.Commands.Tests;

public class RedisSingleInstanceFixture : IAsyncLifetime
{
    public readonly RedisContainer SingleNode = CreateNode($"single-node-{Guid.NewGuid()}").Build();
    
    public async Task InitializeAsync() => await SingleNode.StartAsync();

    public async Task DisposeAsync()
    {
        await SingleNode.StopAsync();
        await SingleNode.DisposeAsync();
    }
    
    private static RedisBuilder CreateNode(string name) => new RedisBuilder()
        .WithName(name)
        .WithImage("redis:7.0")
        .WithPortBinding(61_378, 6_378)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"));
}