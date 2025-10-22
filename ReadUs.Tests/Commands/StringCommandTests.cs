using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ReadUs.Commands;
using Xunit;

namespace ReadUs.Tests.Commands;

[Collection(nameof(RedisSingleInstanceFixtureCollection))]
public class StringCommandTests(RedisSingleInstanceFixture fixture) : IDisposable
{
    private readonly IRedisConnectionPool _pool =
        RedisConnectionPool.Create(new Uri($"redis://{fixture.SingleNode.GetConnectionString()}"));
    
    public void Dispose() => _pool.Dispose();

    [Fact]
    public async Task GetCommandRetrievesValues()
    {
        var testKey = Guid.NewGuid().ToString("N");

        var commands = await _pool.GetDatabase();

        await commands.Set(testKey, "The quick brown fox jumped over the lazy moon.");

        var retrievedValue = (await commands.Get(testKey)).Unwrap();

        Assert.Equal("The quick brown fox jumped over the lazy moon.", retrievedValue);
    }
        
    [Fact]
    public async Task SetCommandAssignsValue()
    {
        var testKey = Guid.NewGuid().ToString("N");

        var commands = await _pool.GetDatabase();

        await commands.Set(testKey, "Never eat soggy waffles.");

        var retrievedValue = (await commands.Get(testKey)).Unwrap();

        Assert.Equal("Never eat soggy waffles.", retrievedValue);
    }    
    
    [Fact]
    public async Task SetCommandCanSetMultiple()
    {
        var baseTestKey = $"{Guid.NewGuid():N}:";

        var firstKey = $"{baseTestKey}1";
        var secondKey = $"{baseTestKey}2";

        var commands = await _pool.GetDatabase();

        var keysAndValues = new[]
        {
            new KeyValuePair<RedisKey, string>(firstKey, "testing"),
            new KeyValuePair<RedisKey, string>(secondKey, "testing")
        };

        await commands.SetMultiple(keysAndValues);
    }
}