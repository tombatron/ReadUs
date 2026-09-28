using System.Text;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Scripting;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises <see cref="RedisScript"/> (project spec §10) against a real, disposable
/// Testcontainers-managed server (project spec §9.2).
/// </summary>
[Collection(StandaloneRedisCollection.Name)]
public class ScriptingTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task EvaluatingANeverLoadedScriptSucceedsViaTheNoscriptToEvalFallback()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        var script = new RedisScript("return redis.call('SET', KEYS[1], ARGV[1])");
        var key = Encoding.UTF8.GetBytes($"readus:test:script:{Guid.NewGuid():N}");

        var result = await script.EvaluateAsync(client, [key], ["hello"u8.ToArray()]);
        Assert.Equal("OK", result.AsString());

        var getResult = await client.GetAsync(key);
        Assert.Equal("hello", getResult.AsString());
    }

    [Fact]
    public async Task EvaluatingAgainAfterAServerSideScriptFlushStillSucceeds()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);

        var script = new RedisScript("return redis.call('SET', KEYS[1], ARGV[1])");
        var key = Encoding.UTF8.GetBytes($"readus:test:script:{Guid.NewGuid():N}");

        var first = await script.EvaluateAsync(client, [key], ["v1"u8.ToArray()]);
        Assert.Equal("OK", first.AsString());

        // The server has now genuinely forgotten this script — proves the fallback
        // actually self-heals a server-side cache eviction, not just "it returns the
        // right value twice."
        var flushResult = await client.ExecuteAsync("SCRIPT"u8.ToArray(), ["FLUSH"u8.ToArray()]);
        Assert.Equal("OK", flushResult.AsString());

        var second = await script.EvaluateAsync(client, [key], ["v2"u8.ToArray()]);
        Assert.Equal("OK", second.AsString());

        var getResult = await client.GetAsync(key);
        Assert.Equal("v2", getResult.AsString());
    }
}
