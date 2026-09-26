using System.Buffers;
using System.Buffers.Text;

namespace ReadUs.Protocol;

/// <summary>
/// Zero-copy-at-the-boundary RESP2/RESP3 tokenizer. <see cref="TryParse"/> attempts to
/// parse exactly one top-level value from the front of a <see cref="ReadOnlySequence{T}"/>
/// of bytes read off the wire.
///
/// Design note (deferred decision, see docs/design/state-machines.md §5): rather than
/// carrying persisted parser state across partial reads (the way <c>Utf8JsonReader</c>
/// does with its heap-storable state struct), this reader re-parses an incomplete frame
/// from scratch on every call once more bytes arrive. A <see cref="SequenceReader{T}"/>
/// is a struct copy of the input, so an aborted attempt costs nothing beyond the
/// boundary-scan it already did — except for aggregate types, where already-materialized
/// child elements are re-allocated on the retry. Redis replies are overwhelmingly small
/// relative to a pipe segment, so a fully-buffered frame is the common case and this
/// costs nothing there; it only repeats work for a reply that itself straddles multiple
/// reads, which is rare enough (large arrays / big bulk payloads) to not be worth a
/// persisted-state parser until benchmarking says otherwise.
/// </summary>
public static class RespFrameReader
{
    public static bool TryParse(ref ReadOnlySequence<byte> buffer, out RedisResult result)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (TryParseValue(ref reader, out result))
        {
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryParseValue(ref SequenceReader<byte> reader, out RedisResult result)
    {
        result = default;

        if (!reader.TryRead(out byte prefix))
        {
            return false;
        }

        return prefix switch
        {
            (byte)'+' => TryParseLine(ref reader, RespType.SimpleString, out result),
            (byte)'-' => TryParseLine(ref reader, RespType.Error, out result),
            (byte)'(' => TryParseLine(ref reader, RespType.BigNumber, out result),
            (byte)':' => TryParseInteger(ref reader, out result),
            (byte)'#' => TryParseBoolean(ref reader, out result),
            (byte)',' => TryParseDouble(ref reader, out result),
            (byte)'_' => TryParseNull(ref reader, out result),
            (byte)'$' => TryParseBulk(ref reader, RespType.BulkString, out result),
            (byte)'!' => TryParseBulk(ref reader, RespType.BulkError, out result),
            (byte)'=' => TryParseBulk(ref reader, RespType.VerbatimString, out result),
            (byte)'*' => TryParseAggregate(ref reader, RespType.Array, out result),
            (byte)'~' => TryParseAggregate(ref reader, RespType.Set, out result),
            (byte)'>' => TryParseAggregate(ref reader, RespType.Push, out result),
            (byte)'%' => TryParseMap(ref reader, out result),
            _ => throw new RespProtocolException($"Unrecognized RESP type prefix 0x{prefix:X2}."),
        };
    }

    private static bool TryParseLine(ref SequenceReader<byte> reader, RespType type, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var line))
        {
            return false;
        }

        result = RedisResult.FromBytes(type, line.ToArray());
        return true;
    }

    private static bool TryParseInteger(ref SequenceReader<byte> reader, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var line))
        {
            return false;
        }

        var span = ToSpan(line);
        if (!Utf8Parser.TryParse(span, out long value, out int consumed) || consumed != span.Length)
        {
            throw new RespProtocolException("Malformed integer value.");
        }

        result = RedisResult.FromInteger(value);
        return true;
    }

    private static bool TryParseBoolean(ref SequenceReader<byte> reader, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var line))
        {
            return false;
        }

        var span = ToSpan(line);
        if (span.Length != 1)
        {
            throw new RespProtocolException("Malformed boolean value.");
        }

        result = span[0] switch
        {
            (byte)'t' => RedisResult.FromBoolean(true),
            (byte)'f' => RedisResult.FromBoolean(false),
            _ => throw new RespProtocolException("Invalid boolean value."),
        };
        return true;
    }

    private static bool TryParseDouble(ref SequenceReader<byte> reader, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var line))
        {
            return false;
        }

        var span = ToSpan(line);

        double value;
        if (span.SequenceEqual("inf"u8))
        {
            value = double.PositiveInfinity;
        }
        else if (span.SequenceEqual("-inf"u8))
        {
            value = double.NegativeInfinity;
        }
        else if (span.SequenceEqual("nan"u8))
        {
            value = double.NaN;
        }
        else if (!Utf8Parser.TryParse(span, out value, out int consumed) || consumed != span.Length)
        {
            throw new RespProtocolException("Malformed double value.");
        }

        result = RedisResult.FromDouble(value);
        return true;
    }

    private static bool TryParseNull(ref SequenceReader<byte> reader, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var line))
        {
            return false;
        }

        if (line.Length != 0)
        {
            throw new RespProtocolException("Malformed null value.");
        }

        result = RedisResult.Null;
        return true;
    }

    private static bool TryParseBulk(ref SequenceReader<byte> reader, RespType type, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var lengthLine))
        {
            return false;
        }

        long length = ParseLengthLine(lengthLine);

        if (length == -1)
        {
            result = RedisResult.Null;
            return true;
        }

        if (length < -1)
        {
            throw new RespProtocolException("Negative bulk length.");
        }

        if (reader.Remaining < length + 2)
        {
            return false;
        }

        var bytes = new byte[length];
        reader.Sequence.Slice(reader.Position, length).CopyTo(bytes);
        reader.Advance(length);

        if (!reader.TryRead(out byte cr) || cr != (byte)'\r' || !reader.TryRead(out byte lf) || lf != (byte)'\n')
        {
            throw new RespProtocolException("Bulk value missing terminating CRLF.");
        }

        result = RedisResult.FromBytes(type, bytes);
        return true;
    }

    private static bool TryParseAggregate(ref SequenceReader<byte> reader, RespType type, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var lengthLine))
        {
            return false;
        }

        long count = ParseLengthLine(lengthLine);

        if (count == -1)
        {
            result = RedisResult.Null;
            return true;
        }

        if (count < -1)
        {
            throw new RespProtocolException("Negative aggregate length.");
        }

        int itemCount = checked((int)count);
        var items = itemCount == 0 ? [] : new RedisResult[itemCount];

        for (int i = 0; i < itemCount; i++)
        {
            if (!TryParseValue(ref reader, out items[i]))
            {
                return false;
            }
        }

        result = RedisResult.FromItems(type, items);
        return true;
    }

    private static bool TryParseMap(ref SequenceReader<byte> reader, out RedisResult result)
    {
        result = default;

        if (!TryReadLine(ref reader, out var lengthLine))
        {
            return false;
        }

        long pairCount = ParseLengthLine(lengthLine);

        if (pairCount == -1)
        {
            result = RedisResult.Null;
            return true;
        }

        if (pairCount < -1)
        {
            throw new RespProtocolException("Negative map length.");
        }

        int itemCount = checked((int)(pairCount * 2));
        var items = itemCount == 0 ? [] : new RedisResult[itemCount];

        for (int i = 0; i < itemCount; i++)
        {
            if (!TryParseValue(ref reader, out items[i]))
            {
                return false;
            }
        }

        // Flattened as alternating key/value, matching RedisResult's documented contract.
        result = RedisResult.FromItems(RespType.Map, items);
        return true;
    }

    private static long ParseLengthLine(in ReadOnlySequence<byte> line)
    {
        var span = ToSpan(line);
        if (!Utf8Parser.TryParse(span, out long value, out int consumed) || consumed != span.Length)
        {
            throw new RespProtocolException("Malformed length header.");
        }

        return value;
    }

    private static bool TryReadLine(ref SequenceReader<byte> reader, out ReadOnlySequence<byte> line)
    {
        if (!reader.TryReadTo(out ReadOnlySequence<byte> raw, (byte)'\n', advancePastDelimiter: true))
        {
            line = default;
            return false;
        }

        line = raw.Length > 0 && ToSpan(raw.Slice(raw.Length - 1))[0] == (byte)'\r'
            ? raw.Slice(0, raw.Length - 1)
            : raw;

        return true;
    }

    private static ReadOnlySpan<byte> ToSpan(in ReadOnlySequence<byte> sequence) =>
        sequence.IsSingleSegment ? sequence.FirstSpan : sequence.ToArray();
}
