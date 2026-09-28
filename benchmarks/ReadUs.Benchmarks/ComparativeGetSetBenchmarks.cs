using System.Net;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ReadUs.Connections;
using ReadUs.Generated;
using StackExchange.Redis;
using ReadUsResult = ReadUs.Protocol.RedisResult;

namespace ReadUs.Benchmarks;

/// <summary>
/// ReadUs vs. StackExchange.Redis, single command at a time (pipeline depth 1), same
/// payload sizes as <see cref="RoundTripLatencyBenchmarks"/>, against the same real
/// standalone <c>redis-server</c> on localhost:6379. Same connection count (1) on both
/// sides, same key, same randomly-generated payload bytes for a given
/// <see cref="PayloadSize"/> run — <see cref="[Benchmark(Baseline = true)]"/> on ReadUs
/// so BenchmarkDotNet's ratio column reads directly as "how ReadUs compares."
///
/// Methodology note: this is a single-machine, single-point-in-time measurement, not a
/// universal claim about either library — see docs/benchmarks.md for the actual
/// numbers and how to reproduce them yourself.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ComparativeGetSetBenchmarks
{
    private RedisClient _readUsClient = null!;
    private ConnectionMultiplexer _seRedisMuxer = null!;
    private IDatabase _seRedisDb = null!;

    private byte[] _key = null!;
    private byte[] _value = null!;
    private RedisKey _seRedisKey;
    private RedisValue _seRedisValue;

    [Params(16, 256, 4096)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _readUsClient = await RedisClient.ConnectAsync(
            new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) },
            connectionCount: 1);

        _seRedisMuxer = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        _seRedisDb = _seRedisMuxer.GetDatabase();

        _key = "readus:bench:compare:key"u8.ToArray();
        _value = new byte[PayloadSize];
        Random.Shared.NextBytes(_value);

        _seRedisKey = _key;
        _seRedisValue = _value;

        await _readUsClient.SetAsync(_key, _value);
        await _seRedisDb.StringSetAsync(_seRedisKey, _seRedisValue);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _readUsClient.DisposeAsync();
        await _seRedisMuxer.DisposeAsync();
    }

    [BenchmarkCategory("Get"), Benchmark(Baseline = true)]
    public Task<ReadUsResult> ReadUsGet() => _readUsClient.GetAsync(_key).AsTask();

    [BenchmarkCategory("Get"), Benchmark]
    public Task<RedisValue> StackExchangeRedisGet() => _seRedisDb.StringGetAsync(_seRedisKey);

    [BenchmarkCategory("Set"), Benchmark(Baseline = true)]
    public Task<ReadUsResult> ReadUsSet() => _readUsClient.SetAsync(_key, _value).AsTask();

    [BenchmarkCategory("Set"), Benchmark]
    public Task<bool> StackExchangeRedisSet() => _seRedisDb.StringSetAsync(_seRedisKey, _seRedisValue);
}
