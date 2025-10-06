using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ReadUs.Tests;

[Collection(nameof(RedisSingleInstanceFixtureCollection))]
public class RedisConnectionTests(RedisSingleInstanceFixture redisSingleInstanceFixture, ITestOutputHelper output)
{
    [Fact]
    public async Task ItWillReturnAllSlotsAvailable()
    {
        using var connection = new RedisConnection(redisSingleInstanceFixture.GetConnectionString());

        await connection.ConnectAsync();

        var slots = (await connection.SlotsAsync()).Unwrap();
        
        Assert.Equal(0, slots.SlotRanges.First().Begin);
        Assert.Equal(16_384, slots.SlotRanges.First().End);
    }
}