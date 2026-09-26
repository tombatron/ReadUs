using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace ReadUs.Protocol;

/// <summary>
/// A fully materialized RESP reply. Scalar payloads (bulk strings, simple strings,
/// errors, verbatim strings, big numbers) are copied once out of the connection's
/// pipe buffer into a caller-owned array at parse time — the pipe buffer is recycled
/// as soon as the frame is consumed, so this copy is unavoidable at the point a value
/// crosses out of the read loop. Decoding beyond that (UTF-8 → string, digit parsing)
/// is deferred until the caller actually asks for it.
///
/// Deferred decision (see docs/design/state-machines.md §5): the backing byte array
/// is a plain heap allocation for now, not pooled. Pooling result buffers needs an
/// ownership/disposal protocol (who returns the array, and when) that is worth doing
/// once benchmarks show it matters, not speculatively.
/// </summary>
public readonly struct RedisResult
{
    private readonly long _int64;
    private readonly double _double;
    private readonly byte[]? _bytes;
    private readonly RedisResult[]? _items;

    public RespType Type { get; }

    private RedisResult(RespType type, long int64Value = 0, double doubleValue = 0, byte[]? bytes = null, RedisResult[]? items = null)
    {
        Type = type;
        _int64 = int64Value;
        _double = doubleValue;
        _bytes = bytes;
        _items = items;
    }

    public static readonly RedisResult Null = new(RespType.Null);

    public static RedisResult FromInteger(long value) => new(RespType.Integer, int64Value: value);

    public static RedisResult FromBoolean(bool value) => new(RespType.Boolean, int64Value: value ? 1 : 0);

    public static RedisResult FromDouble(double value) => new(RespType.Double, doubleValue: value);

    public static RedisResult FromBytes(RespType type, byte[] bytes) => new(type, bytes: bytes);

    public static RedisResult FromItems(RespType type, RedisResult[] items) => new(type, items: items);

    public bool IsNull => Type == RespType.Null;

    public bool IsError => Type is RespType.Error or RespType.BulkError;

    public long AsInt64() => Type switch
    {
        RespType.Integer => _int64,
        RespType.Boolean => _int64,
        RespType.BulkString or RespType.SimpleString or RespType.VerbatimString or RespType.BigNumber
            when _bytes is not null && Utf8Parser.TryParse(_bytes, out long value, out _) => value,
        _ => throw new InvalidOperationException($"Cannot read {Type} as Int64."),
    };

    public bool AsBoolean() => Type switch
    {
        RespType.Boolean => _int64 != 0,
        RespType.Integer => _int64 != 0,
        _ => throw new InvalidOperationException($"Cannot read {Type} as Boolean."),
    };

    public double AsDouble() => Type switch
    {
        RespType.Double => _double,
        RespType.Integer => _int64,
        RespType.BulkString or RespType.SimpleString or RespType.VerbatimString
            when _bytes is not null && Utf8Parser.TryParse(_bytes, out double value, out _) => value,
        _ => throw new InvalidOperationException($"Cannot read {Type} as Double."),
    };

    public ReadOnlySpan<byte> AsSpan() => Type switch
    {
        RespType.BulkString or RespType.SimpleString or RespType.Error or RespType.BulkError
            or RespType.VerbatimString or RespType.BigNumber => _bytes,
        _ => throw new InvalidOperationException($"Cannot read {Type} as a byte span."),
    };

    public string AsString() => Type == RespType.Null ? null! : Encoding.UTF8.GetString(AsSpan());

    public ReadOnlySpan<RedisResult> AsItems() => Type switch
    {
        RespType.Array or RespType.Set or RespType.Push => _items,
        RespType.Map => _items,
        _ => throw new InvalidOperationException($"Cannot read {Type} as an item list."),
    };

    public override string ToString() => Type switch
    {
        RespType.Null => "(nil)",
        RespType.Integer or RespType.Boolean => AsInt64().ToString(CultureInfo.InvariantCulture),
        RespType.Double => AsDouble().ToString("G17", CultureInfo.InvariantCulture),
        RespType.Array or RespType.Set or RespType.Push or RespType.Map => $"{Type}[{_items?.Length ?? 0}]",
        _ => AsString(),
    };
}
