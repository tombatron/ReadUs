using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ReadUs.Connections;
using ReadUs.Generated;
using StackExchange.Redis;

namespace ReadUs.Benchmarks;

/// <summary>
/// ReadUs vs. StackExchange.Redis, pipelined <c>PING</c> throughput across the same
/// depths as <see cref="PipelineDepthBenchmarks"/>, against the same real standalone
/// server. Same connection count (4) on both sides.
///
/// Methodology note: this is a single-machine, single-point-in-time measurement, not a
/// universal claim about either library — see docs/benchmarks.md for the actual
/// numbers and how to reproduce them yourself.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ComparativePipelineDepthBenchmarks
{
    private RedisClient _readUsClient = null!;
    private ConnectionMultiplexer _seRedisMuxer = null!;
    private IDatabase _seRedisDb = null!;

    [Params(1, 8, 64, 512)]
    public int Depth { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _readUsClient = await RedisClient.ConnectAsync(
            new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) },
            connectionCount: 4);

        _seRedisMuxer = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        _seRedisDb = _seRedisMuxer.GetDatabase();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _readUsClient.DisposeAsync();
        await _seRedisMuxer.DisposeAsync();
    }

    [BenchmarkCategory("Pipeline"), Benchmark(Baseline = true)]
    public async Task ReadUsPipelinedPings()
    {
        var tasks = new Task[Depth];
        for (var i = 0; i < Depth; i++)
        {
            tasks[i] = _readUsClient.PingAsync().AsTask();
        }

        await Task.WhenAll(tasks);
    }

    [BenchmarkCategory("Pipeline"), Benchmark]
    public async Task StackExchangeRedisPipelinedPings()
    {
        var tasks = new Task[Depth];
        for (var i = 0; i < Depth; i++)
        {
            tasks[i] = _seRedisDb.PingAsync();
        }

        await Task.WhenAll(tasks);
    }
}
