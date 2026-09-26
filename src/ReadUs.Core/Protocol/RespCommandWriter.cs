using System.Buffers;
using System.Buffers.Text;

namespace ReadUs.Protocol;

/// <summary>
/// Serializes a Redis command as a RESP array of bulk strings directly into a pooled
/// buffer (typically a <see cref="System.IO.Pipelines.PipeWriter"/>). No intermediate
/// object array, no boxing, no string formatting — argument lengths are written with
/// <see cref="Utf8Formatter"/> straight into the destination span.
/// </summary>
public static class RespCommandWriter
{
    public static void WriteCommand(IBufferWriter<byte> writer, ReadOnlySpan<byte> commandName, ReadOnlySpan<ReadOnlyMemory<byte>> args)
    {
        WriteHeader(writer, (byte)'*', 1 + args.Length);
        WriteBulkString(writer, commandName);

        foreach (var arg in args)
        {
            WriteBulkString(writer, arg.Span);
        }
    }

    public static void WriteBulkString(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        WriteHeader(writer, (byte)'$', value.Length);

        var span = writer.GetSpan(value.Length + 2);
        value.CopyTo(span);
        span[value.Length] = (byte)'\r';
        span[value.Length + 1] = (byte)'\n';
        writer.Advance(value.Length + 2);
    }

    private static void WriteHeader(IBufferWriter<byte> writer, byte prefix, int number)
    {
        // prefix + up to 11 digits (int, incl. sign) + CRLF; 16 leaves comfortable margin.
        var span = writer.GetSpan(16);
        span[0] = prefix;

        if (!Utf8Formatter.TryFormat(number, span[1..], out int written))
        {
            throw new InvalidOperationException("Unreachable: header buffer too small for a 32-bit count.");
        }

        span[1 + written] = (byte)'\r';
        span[2 + written] = (byte)'\n';
        writer.Advance(3 + written);
    }
}
