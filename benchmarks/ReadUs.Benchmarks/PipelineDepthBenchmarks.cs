using System.Net;
using BenchmarkDotNet.Attributes;
using ReadUs.Connections;
using ReadUs.Generated;

namespace ReadUs.Benchmarks;

/// <summary>
/// Pipelined throughput across a range of pipeline depths (project spec §8) — the
/// number of commands issued concurrently against a fixed-size Tier 1 pool before
/// awaiting all of them. Exercises the FIFO reply-correlation path under real
/// contention, and shows how allocation and latency scale as depth grows.
/// </summary>
[MemoryDiagnoser]
public class PipelineDepthBenchmarks
{
    private RedisClient _client = null!;

    [Params(1, 8, 64, 512)]
    public int Depth { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _client = await RedisClient.ConnectAsync(
            new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) },
            connectionCount: 4);
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _client.DisposeAsync();

    [Benchmark]
    public async Task PipelinedPings()
    {
        var tasks = new Task[Depth];
        for (int i = 0; i < Depth; i++)
        {
            tasks[i] = _client.PingAsync().AsTask();
        }

        await Task.WhenAll(tasks);
    }
}
