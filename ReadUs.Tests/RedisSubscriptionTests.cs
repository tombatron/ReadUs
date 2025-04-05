using System;
using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests;

public sealed class RedisSubscriptionTests(RedisSingleInstanceFixture fixture) : IClassFixture<RedisSingleInstanceFixture>
{
    [Fact]
    public async Task ItCanSubscribeAndUnsubscribe()
    {
        var pool = RedisConnectionPool.Create(fixture.GetConnectionString());

        var db = await pool.GetAsync();

        string subscriptionMessage = "got nothing";

        var subscription = await db.Subscribe("channel", (message) =>
        {
            subscriptionMessage = message;
        });
        
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        
        await db.Publish("channel", "hello world");
        
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        
        Assert.Equal("hello world", subscriptionMessage);
    }
}