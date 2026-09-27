using System.Text;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Targets the harder shapes the command-table source generator (project spec §13
/// step 4) flattens — a top-level <c>oneof</c> (SET's NX/XX, EXPIRE's condition flags)
/// and a top-level <c>block</c>, both non-repeating (SORT's LIMIT offset/count, a
/// nullable tuple) and repeating (HSET's field/value pairs, a list of tuples) — against
/// a real server, not just checking that the generated code compiles.
/// </summary>
[Collection(StandaloneRedisCollection.Name)]
public class GeneratedCommandsTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task SetNxOnlySetsWhenTheKeyIsAbsent()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:gen:setnx:{Guid.NewGuid():N}");

        var first = await client.SetAsync(key, "first"u8.ToArray(), conditionNx: true);
        Assert.Equal("OK", first.AsString());

        var second = await client.SetAsync(key, "second"u8.ToArray(), conditionNx: true);
        Assert.True(second.IsNull);

        var value = await client.GetAsync(key);
        Assert.Equal("first", value.AsString());
    }

    [Fact]
    public async Task SetWithExpirationSecondsAppliesATtl()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:gen:setex:{Guid.NewGuid():N}");

        await client.SetAsync(key, "value"u8.ToArray(), expirationSeconds: 100);

        var ttl = await client.ExecuteAsync("TTL"u8.ToArray(), [key]);
        Assert.InRange(ttl.AsInt64(), 1, 100);
    }

    [Fact]
    public async Task ExpireConditionNxFailsOnAKeyThatAlreadyHasATtl()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:gen:expirenx:{Guid.NewGuid():N}");
        await client.SetAsync(key, "value"u8.ToArray());

        var first = await client.ExpireAsync(key, 100, conditionNx: true);
        Assert.Equal(1, first.AsInt64());

        var second = await client.ExpireAsync(key, 200, conditionNx: true);
        Assert.Equal(0, second.AsInt64());
    }

    [Fact]
    public async Task HsetWritesEveryFieldValuePairFromTheTupleList()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:gen:hset:{Guid.NewGuid():N}");

        var added = await client.HsetAsync(key,
        [
            ("field1"u8.ToArray(), "a"u8.ToArray()),
            ("field2"u8.ToArray(), "b"u8.ToArray()),
        ]);

        Assert.Equal(2, added.AsInt64());

        var field1 = await client.HgetAsync(key, "field1"u8.ToArray());
        var field2 = await client.HgetAsync(key, "field2"u8.ToArray());
        Assert.Equal("a", field1.AsString());
        Assert.Equal("b", field2.AsString());
    }

    [Fact]
    public async Task SortWithLimitAppliesTheNullableTupleGroup()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:gen:sort:{Guid.NewGuid():N}");
        await client.RpushAsync(key, ["3"u8.ToArray(), "1"u8.ToArray(), "2"u8.ToArray(), "4"u8.ToArray()]);

        var result = await client.SortAsync(key, limit: (1, 2));

        var items = result.AsItems();
        Assert.Equal(2, items.Length);
        Assert.Equal("2", items[0].AsString());
        Assert.Equal("3", items[1].AsString());
    }
}
