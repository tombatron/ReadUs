using Xunit;

namespace ReadUs.Commands.Tests;

[Collection(nameof(RedisSingleInstanceFixtureCollection))]
public class ListCommandTests(RedisSingleInstanceFixture fixture) : IDisposable
{
    private readonly IRedisConnectionPool _pool =
        RedisConnectionPool.Create(new Uri($"redis://{fixture.SingleNode.GetConnectionString()}"));
    
    public void Dispose() => _pool.Dispose();
    
    [Fact]
    public async Task ListLengthCommandGetsLength()
    {
        var testKey = Guid.NewGuid().ToString("N");

        var commands = await _pool.GetDatabase();

        var initialLength = (await commands.ListLength(testKey)).Unwrap();

        await commands.LeftPush(testKey, ["Yo"]);

        var finalLength = (await commands.ListLength(testKey)).Unwrap();

        Assert.Equal(0, initialLength);
        Assert.Equal(1, finalLength);
    }
}