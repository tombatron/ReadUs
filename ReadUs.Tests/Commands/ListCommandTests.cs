using System;
using System.Threading.Tasks;
using ReadUs.Commands;
using Xunit;

namespace ReadUs.Tests.Commands;

[Collection(nameof(RedisSingleInstanceFixtureCollection))]
public sealed class ListCommandTests(RedisSingleInstanceFixture fixture) 
{
    [Fact]
    public async Task ListLengthCommandGetsLength()
    {
        using var pool = RedisConnectionPool.Create(fixture.GetConnectionString());
        
        var testKey = Guid.NewGuid().ToString("N");

        var commands = pool.GetDatabase();

        var initialLength = (await commands.ListLength(testKey)).Unwrap();

        await commands.LeftPush(testKey, ["Yo"]);

        var finalLength = (await commands.ListLength(testKey)).Unwrap();

        Assert.Equal(0, initialLength);
        Assert.Equal(1, finalLength);
    }
}