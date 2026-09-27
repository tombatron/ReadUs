using System.Text;
using ReadUs.Caching;
using ReadUs.Connections;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>Exercises the RESP3 CLIENT TRACKING-backed client-side cache (project spec §10) against a real, disposable Testcontainers-managed server (project spec §9.2).</summary>
[Collection(StandaloneRedisCollection.Name)]
public class ClientSideCacheTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task ReadThroughPopulatesTheCacheAndSubsequentReadsHitLocally()
    {
        await using var admin = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        await using var cache = await ClientSideCache.ConnectAsync(Options);

        var key = $"readus:test:cache:{Guid.NewGuid():N}";
        await admin.ExecuteAsync("SET"u8.ToArray(), [Encoding.UTF8.GetBytes(key), "v1"u8.ToArray()]);

        Assert.False(cache.TryGetCached(key, out _));

        var first = await cache.GetAsync(key);
        Assert.Equal("v1", first.AsString());
        Assert.True(cache.TryGetCached(key, out var cached));
        Assert.Equal("v1", cached.AsString());
    }

    [Fact]
    public async Task WritingTheKeyElsewhereInvalidatesTheCacheEntry()
    {
        await using var admin = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        await using var cache = await ClientSideCache.ConnectAsync(Options);

        var key = $"readus:test:cache:{Guid.NewGuid():N}";
        await admin.ExecuteAsync("SET"u8.ToArray(), [Encoding.UTF8.GetBytes(key), "v1"u8.ToArray()]);

        var first = await cache.GetAsync(key);
        Assert.Equal("v1", first.AsString());
        Assert.True(cache.TryGetCached(key, out _));

        await admin.ExecuteAsync("SET"u8.ToArray(), [Encoding.UTF8.GetBytes(key), "v2"u8.ToArray()]);

        // The invalidation push is asynchronous — poll briefly rather than assuming an
        // exact delivery time.
        var evicted = false;
        for (var i = 0; i < 50 && !evicted; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            evicted = !cache.TryGetCached(key, out _);
        }

        Assert.True(evicted, "Expected the invalidation push to evict the cache entry.");

        var second = await cache.GetAsync(key);
        Assert.Equal("v2", second.AsString());
    }

    [Fact]
    public async Task FlushallInvalidatesTheEntireCache()
    {
        await using var admin = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        await using var cache = await ClientSideCache.ConnectAsync(Options);

        var key = $"readus:test:cache:{Guid.NewGuid():N}";
        await admin.ExecuteAsync("SET"u8.ToArray(), [Encoding.UTF8.GetBytes(key), "v1"u8.ToArray()]);
        await cache.GetAsync(key);
        Assert.True(cache.TryGetCached(key, out _));

        await admin.ExecuteAsync("FLUSHALL"u8.ToArray(), []);

        var cleared = false;
        for (var i = 0; i < 50 && !cleared; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            cleared = cache.Count == 0;
        }

        Assert.True(cleared, "Expected FLUSHALL's null-payload invalidation to clear the whole cache.");
    }

    [Fact]
    public async Task NeverCachesAValueThatWasAlreadyInvalidatedWhileTheReadWasInFlight()
    {
        // Stress the exact race the cache's own design doc comment describes: a write
        // landing in the narrow window between the GET reply arriving on the wire and
        // this method's continuation actually populating the cache.
        //
        // Note what this test does and doesn't assert: if the concurrent SET commits
        // server-side *after* our GET, the resulting invalidation is a genuine
        // asynchronous network round trip away — the cache legitimately (and
        // correctly) shows "v1" for a brief, unbounded-but-finite moment until that
        // push arrives, the same as any push-invalidated cache's normal eventual
        // consistency. That is not the bug being tested for. The bug this guards
        // against is the invalidation being *lost* — arriving while the read was still
        // in flight, finding nothing yet in the cache to evict, and the read then
        // caching a value already known to be stale with nothing left to ever correct
        // it. So each iteration polls for the cache to *settle* rather than sampling
        // once immediately, and only fails if it settles on the wrong value.
        await using var admin = await RedisClient.ConnectAsync(Options, connectionCount: 4);
        await using var cache = await ClientSideCache.ConnectAsync(Options);

        for (var i = 0; i < 50; i++)
        {
            var key = $"readus:test:cache:race:{i}:{Guid.NewGuid():N}";
            var keyBytes = Encoding.UTF8.GetBytes(key);
            await admin.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "v1"u8.ToArray()]);

            var readTask = cache.GetAsync(key).AsTask();
            var writeTask = admin.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "v2"u8.ToArray()]).AsTask();
            await Task.WhenAll(readTask, writeTask);

            // Generous settle budget: under the full test suite's concurrent load
            // against a single shared, single-threaded Redis instance, an
            // invalidation's actual delivery can queue up behind everything else
            // hitting the same server — that's real, expected latency, not a bug (see
            // the remarks above).
            string? settledValue = null;
            for (var poll = 0; poll < 500; poll++)
            {
                if (!cache.TryGetCached(key, out var cachedValue))
                {
                    settledValue = null;
                    break;
                }

                var asString = cachedValue.AsString();
                if (asString == "v2")
                {
                    settledValue = asString;
                    break;
                }

                settledValue = asString; // still "v1" (or mid-transition) — keep polling
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            Assert.NotEqual("v1", settledValue);
        }
    }
}
