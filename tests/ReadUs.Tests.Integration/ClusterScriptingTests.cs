using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Scripting;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises <see cref="ClusterScriptingExtensions.EvaluateAsync"/> against a real,
/// disposable Testcontainers-managed Cluster (project spec §9.2). <c>EVAL</c>/
/// <c>EVALSHA</c> are <c>HasUnknownKeys</c> in the vendored command table (see
/// <see cref="ClusterScriptingExtensions"/>'s own remarks), so this specifically proves
/// the whole round trip still succeeds via <see cref="ClusterClient"/>'s existing
/// MOVED-redirect-following even when the first routing guess lands on the wrong node.
/// </summary>
[Collection(ClusterRedisCollection.Name)]
public class ClusterScriptingTests(ClusterRedisFixture fixture)
{
    private IReadOnlyList<EndPoint> SeedEndpoints => fixture.SeedEndpoints;

    [Fact]
    public async Task EvaluatingAScriptWithAKeySucceedsRegardlessOfWhichNodeItFirstRoutesTo()
    {
        await using var cluster = await ClusterClient.ConnectAsync(SeedEndpoints);

        var script = new RedisScript("return redis.call('SET', KEYS[1], ARGV[1])");
        var key = Encoding.UTF8.GetBytes($"readus:test:cluster:script:{Guid.NewGuid():N}");

        var result = await script.EvaluateAsync(cluster, [key], ["hello"u8.ToArray()]);
        Assert.Equal("OK", result.AsString());

        var getResult = await cluster.ExecuteAsync("GET"u8.ToArray(), [key]);
        Assert.Equal("hello", getResult.AsString());
    }
}
