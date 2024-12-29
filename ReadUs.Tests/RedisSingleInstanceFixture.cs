﻿using System.Collections.Concurrent;
using System.Linq;
using DotNet.Testcontainers.Builders;
using Testcontainers.Redis;

namespace ReadUs.Tests;

public class RedisSingleInstanceFixture : IAsyncLifetime
{
    private static readonly ConcurrentStack<int> Ports = new(Enumerable.Range(60_000, 1_000));

    // public readonly RedisContainer SingleNode = new RedisBuilder()
    //     .WithImage("redis:7.0")
    //     .WithPortBinding(63790, 6379)
    //     .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"))
    //     .Build();
    public readonly RedisContainer SingleNode = CreateNode("single-node").Build();

    public async Task InitializeAsync()
    {
        await SingleNode.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await SingleNode.StopAsync();
        await SingleNode.DisposeAsync();
    }

    public static RedisBuilder CreateNode(string name)
    {
        if (Ports.TryPop(out var port))
            return new RedisBuilder()
                .WithImage("redis:7.0")
                .WithPortBinding(port, 6379)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "PING"));

        throw new Exception("No more ports available");
    }
}