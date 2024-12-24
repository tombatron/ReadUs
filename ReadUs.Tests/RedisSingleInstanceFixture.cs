using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Testcontainers.Redis;
using Xunit;

namespace ReadUs.Tests;

public class RedisSingleInstanceFixture : IAsyncLifetime
{
    public readonly RedisContainer SingleNode = new RedisBuilder()
        .WithImage("redis:7.0")
        .WithPortBinding(63790, 6379)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"))
        .Build();
    
    public async Task InitializeAsync()
    {
        await SingleNode.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await SingleNode.StopAsync();
        await SingleNode.DisposeAsync();
    }
}