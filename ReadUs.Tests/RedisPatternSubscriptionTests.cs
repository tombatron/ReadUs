using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests;

public class RedisPatternSubscriptionTests(RedisSingleInstanceFixture fixture) : IClassFixture<RedisSingleInstanceFixture>
{
    [Fact]
    public async Task ItCanPatternSubscribeAndReceiveMessages()
    {
        var pool = RedisConnectionPool.Create(fixture.GetConnectionString());
                
        var db = await pool.GetAsync();

        var firstChannelMessage = "got nothing";
        var secondChannelMessage = "got nothing";

        var subscription = await db.SubscribeWithPattern("channel*", (pattern, channel, message) =>
        {
            if (channel == "channel1")
            {
                firstChannelMessage = message;
            }

            if (channel == "channel2")
            {
                secondChannelMessage = message;
            }
        });

        await Task.Delay(TimeSpan.FromMilliseconds(1));

        await db.Publish("channel1", "channel_1 got a message");
        await db.Publish("channel2", "channel_2 got a message");
        
        await Task.Delay(TimeSpan.FromMilliseconds(1));

        Assert.Equal("channel_1 got a message", firstChannelMessage);
        Assert.Equal("channel_2 got a message", secondChannelMessage);
    }

    [Fact]
    public async Task ItCanPatternSubscribeAndPatternUnsubscribe()
    {
        var pool = RedisConnectionPool.Create(fixture.GetConnectionString());

        var db = await pool.GetAsync();

        string firstPattern = "got nothing";
        string otherPattern = "got nothing";

        var subscription = await db.SubscribeWithPattern(["channel*", "otherchannel*"], (pattern, channel, message) => 
        {
            if (pattern == "channel*")
            {
                firstPattern = message;
            }

            if (pattern == "otherchannel*")
            {
                otherPattern = message;
            }
        });

        await Task.Delay(TimeSpan.FromMilliseconds(1));

        await db.Publish("channel1", "got a message");
        await db.Publish("otherchannel1", "other channel updated.");

        await Task.Delay(TimeSpan.FromMilliseconds(1));

        await subscription.UnsubscribeWithPattern("otherchannel*");

        await Task.Delay(TimeSpan.FromMilliseconds(1));

        await db.Publish("otherchannel1", "test failed.");

        Assert.Equal("got a message", firstPattern);
        Assert.Equal("other channel updated.", otherPattern);
    }
}

