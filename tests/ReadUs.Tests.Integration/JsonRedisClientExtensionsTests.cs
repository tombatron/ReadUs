using System.Text;
using System.Text.Json.Serialization;
using ReadUs.Connections;
using ReadUs.Extensions.Json;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

public sealed record TestPerson(string Name, int Age);

[JsonSerializable(typeof(TestPerson))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}

/// <summary>Exercises the typed JSON POCO helpers (project spec §10, §13 step 7's lowest-priority "typed helpers" item) against a real, disposable Testcontainers-managed server (project spec §9.2).</summary>
[Collection(StandaloneRedisCollection.Name)]
public class JsonRedisClientExtensionsTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task RoundTripsAPoco()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:json:{Guid.NewGuid():N}");
        var person = new TestPerson("Ada Lovelace", 36);

        await client.SetJsonAsync(key, person, TestJsonContext.Default.TestPerson);
        var result = await client.GetJsonAsync(key, TestJsonContext.Default.TestPerson);

        Assert.Equal(person, result);
    }

    [Fact]
    public async Task GetJsonAsyncReturnsDefaultForAMissingKey()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:json:missing:{Guid.NewGuid():N}");

        var result = await client.GetJsonAsync(key, TestJsonContext.Default.TestPerson);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetJsonAsyncThrowsOnACommandLevelError()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:json:wrongtype:{Guid.NewGuid():N}");
        await client.ExecuteAsync("LPUSH"u8.ToArray(), [key, "not-json"u8.ToArray()]);

        await Assert.ThrowsAsync<RedisConnectionException>(async () =>
            await client.GetJsonAsync(key, TestJsonContext.Default.TestPerson));
    }
}
