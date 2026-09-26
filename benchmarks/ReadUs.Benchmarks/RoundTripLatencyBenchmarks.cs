using System.Net;
using BenchmarkDotNet.Attributes;
using ReadUs.Connections;
using ReadUs.Generated;
using ReadUs.Protocol;

namespace ReadUs.Benchmarks;

/// <summary>
/// Single-command round-trip latency at varying payload sizes, against a real
/// standalone <c>redis-server</c> on localhost:6379 (project spec §8). Pipeline depth
/// 1 — one outstanding request at a time — so this measures pure protocol/transport
/// overhead, not concurrency scaling (see <see cref="PipelineDepthBenchmarks"/> for
/// that).
/// </summary>
[MemoryDiagnoser]
public class RoundTripLatencyBenchmarks
{
    private RedisClient _client = null!;
    private byte[] _key = null!;
    private byte[] _value = null!;

    [Params(16, 256, 4096)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _client = await RedisClient.ConnectAsync(
            new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) },
            connectionCount: 1);

        _key = "readus:bench:key"u8.ToArray();
        _value = new byte[PayloadSize];
        Random.Shared.NextBytes(_value);
        await _client.SetAsync(_key, _value);
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _client.DisposeAsync();

    [Benchmark(Baseline = true)]
    public ValueTask<RedisResult> Ping() => _client.PingAsync();

    [Benchmark]
    public ValueTask<RedisResult> Get() => _client.GetAsync(_key);

    [Benchmark]
    public ValueTask<RedisResult> Set() => _client.SetAsync(_key, _value);
}
