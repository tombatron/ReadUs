using System;
using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests;

public sealed class RedisSubscriptionTests(RedisSingleInstanceFixture fixture) : IClassFixture<RedisSingleInstanceFixture>
{
    [Fact]
    public async Task ItCanSubscribeAndReceiveMessages()
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

    [Fact]
    public async Task ItCanSubscribeAndUnsubscribe()
    {
        var pool = RedisConnectionPool.Create(fixture.GetConnectionString());
        
        var db = await pool.GetAsync();
        
        string channel1Message = "got nothing";
        string channel2Message = "got nothing";

        var subscription = await db.Subscribe(["channel_1", "channel_2"], (channel, message) =>
        {
            if (channel == "channel_1")
            {
                channel1Message = message;   
            }

            if (channel == "channel_2")
            {
                channel2Message = message;
            }
        });
        
        await Task.Delay(TimeSpan.FromMilliseconds(10));
        
        await db.Publish("channel_1", "hello world");
        await db.Publish("channel_2", "hello world");

        await subscription.Unsubscribe("channel_1");
        
        await db.Publish("channel_1", "goodnight moon");
        await db.Publish("channel_2", "goodnight moon");
        
        Assert.Equal("hello world", channel1Message);
        Assert.Equal("goodnight moon", channel2Message);
    }
}