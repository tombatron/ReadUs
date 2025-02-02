using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests.Integration;

public sealed class RedisCommandsTests : IClassFixture<RedisSingleInstanceFixture>, IDisposable
{
    private readonly RedisConnectionPool _pool;

    public RedisCommandsTests(RedisSingleInstanceFixture fixture)
    {
        var connstring = fixture.SingleNode.GetConnectionString();

        _pool = new RedisSingleInstanceConnectionPool(new Uri($"redis://{fixture.SingleNode.GetConnectionString()}"));
    }

    public void Dispose()
    {
        _pool.Dispose();
    }

    [Fact]
    public async Task Select_Changes_Database()
    {
        var testKey = Guid.NewGuid().ToString("N");

        using var commands = await _pool.GetAsync();

        await commands.SelectAsync(10);

        await commands.SetAsync(testKey, "Hello World");

        await commands.SelectAsync(0);

        await commands.SetAsync(testKey, "Goodnight Moon");

        await commands.SelectAsync(10);

        var databaseTenValue = (await commands.GetAsync(testKey)).Unwrap();

        await commands.SelectAsync(0);

        var databaseZeroValue = (await commands.GetAsync(testKey)).Unwrap();

        Assert.Equal("Hello World", databaseTenValue);
        Assert.Equal("Goodnight Moon", databaseZeroValue);
    }

    [Fact]
    public async Task Get_Retrieves_Value()
    {
        var testKey = Guid.NewGuid().ToString("N");

        using var commands = await _pool.GetAsync();

        await commands.SetAsync(testKey, "The quick brown fox jumped over the lazy moon.");

        var retrievedValue = (await commands.GetAsync(testKey)).Unwrap();

        Assert.Equal("The quick brown fox jumped over the lazy moon.", retrievedValue);
    }

    [Fact]
    public async Task Set_Assigns_Value()
    {
        var testKey = Guid.NewGuid().ToString("N");

        using var commands = await _pool.GetAsync();

        await commands.SetAsync(testKey, "Never eat soggy waffles.");

        var retrievedValue = (await commands.GetAsync(testKey)).Unwrap();

        Assert.Equal("Never eat soggy waffles.", retrievedValue);
    }

    [Fact]
    public async Task Llen_Gets_List_Length()
    {
        var testKey = Guid.NewGuid().ToString("N");

        using var commands = await _pool.GetAsync();

        var initialLength = (await commands.ListLengthAsync(testKey)).Unwrap();

        await commands.LeftPushAsync(testKey, "Yo");

        var finalLength = (await commands.ListLengthAsync(testKey)).Unwrap();

        Assert.Equal(0, initialLength);
        Assert.Equal(1, finalLength);
    }

    [Fact]
    public async Task CanSetMultiple()
    {
        var baseTestKey = $"{Guid.NewGuid():N}:";

        var firstKey = $"{baseTestKey}1";
        var secondKey = $"{baseTestKey}2";

        using var commands = await _pool.GetAsync();


        var keysAndValues = new[]
        {
            new KeyValuePair<RedisKey, string>(firstKey, "testing"),
            new KeyValuePair<RedisKey, string>(secondKey, "testing")
        };

        await commands.SetMultipleAsync(keysAndValues);
    }

    [Fact]
    public void Scratch()
    {
        var keys = Enumerable.Range(1, 16000)
            .Select(x => new KeyValuePair<RedisKey, string>(Guid.NewGuid().ToString("N"), "")).ToArray();

        var groups = keys.GroupBy(x => x.Key.Slot);

        foreach (var g in groups) Debug.WriteLine(g);
    }
}