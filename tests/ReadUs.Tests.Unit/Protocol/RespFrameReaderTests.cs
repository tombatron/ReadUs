using System.Buffers;
using System.Text;
using ReadUs.Protocol;

namespace ReadUs.Tests.Unit.Protocol;

public class RespFrameReaderTests
{
    [Fact]
    public void ParsesSimpleString()
    {
        var buffer = SequenceHelpers.Single("+OK\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.SimpleString, result.Type);
        Assert.Equal("OK", result.AsString());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void ParsesError()
    {
        var buffer = SequenceHelpers.Single("-ERR bad thing\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Error, result.Type);
        Assert.True(result.IsError);
        Assert.Equal("ERR bad thing", result.AsString());
    }

    [Theory]
    [InlineData(":1000\r\n", 1000L)]
    [InlineData(":-5\r\n", -5L)]
    [InlineData(":0\r\n", 0L)]
    public void ParsesInteger(string frame, long expected)
    {
        var buffer = SequenceHelpers.Single(Encoding.ASCII.GetBytes(frame));

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Integer, result.Type);
        Assert.Equal(expected, result.AsInt64());
    }

    [Fact]
    public void ParsesBulkString()
    {
        var buffer = SequenceHelpers.Single("$5\r\nhello\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.BulkString, result.Type);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void ParsesEmptyBulkString()
    {
        var buffer = SequenceHelpers.Single("$0\r\n\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(string.Empty, result.AsString());
    }

    [Fact]
    public void ParsesNullBulkString()
    {
        var buffer = SequenceHelpers.Single("$-1\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ParsesNullArray()
    {
        var buffer = SequenceHelpers.Single("*-1\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ParsesFlatArray()
    {
        var buffer = SequenceHelpers.Single("*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Array, result.Type);

        var items = result.AsItems();
        Assert.Equal(2, items.Length);
        Assert.Equal("foo", items[0].AsString());
        Assert.Equal("bar", items[1].AsString());
    }

    [Fact]
    public void ParsesNestedArray()
    {
        var buffer = SequenceHelpers.Single("*2\r\n*1\r\n:1\r\n$3\r\nfoo\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));

        var items = result.AsItems();
        Assert.Equal(2, items.Length);

        var inner = items[0].AsItems();
        Assert.Equal(1, inner.Length);
        Assert.Equal(1, inner[0].AsInt64());
        Assert.Equal("foo", items[1].AsString());
    }

    [Theory]
    [InlineData("#t\r\n", true)]
    [InlineData("#f\r\n", false)]
    public void ParsesBoolean(string frame, bool expected)
    {
        var buffer = SequenceHelpers.Single(Encoding.ASCII.GetBytes(frame));

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Boolean, result.Type);
        Assert.Equal(expected, result.AsBoolean());
    }

    [Theory]
    [InlineData(",3.14\r\n", 3.14)]
    [InlineData(",inf\r\n", double.PositiveInfinity)]
    [InlineData(",-inf\r\n", double.NegativeInfinity)]
    public void ParsesDouble(string frame, double expected)
    {
        var buffer = SequenceHelpers.Single(Encoding.ASCII.GetBytes(frame));

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Double, result.Type);
        Assert.Equal(expected, result.AsDouble());
    }

    [Fact]
    public void ParsesNanDouble()
    {
        var buffer = SequenceHelpers.Single(",nan\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.True(double.IsNaN(result.AsDouble()));
    }

    [Fact]
    public void ParsesBigNumber()
    {
        const string digits = "3492890328409238509324850943850943825024385";
        var buffer = SequenceHelpers.Single(Encoding.ASCII.GetBytes($"({digits}\r\n"));

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.BigNumber, result.Type);
        Assert.Equal(digits, result.AsString());
    }

    [Fact]
    public void ParsesBulkError()
    {
        var buffer = SequenceHelpers.Single("!21\r\nSYNTAX invalid syntax\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.BulkError, result.Type);
        Assert.True(result.IsError);
        Assert.Equal("SYNTAX invalid syntax", result.AsString());
    }

    [Fact]
    public void ParsesVerbatimString()
    {
        var buffer = SequenceHelpers.Single("=15\r\ntxt:Some string\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.VerbatimString, result.Type);
        Assert.Equal("txt:Some string", result.AsString());
    }

    [Fact]
    public void ParsesMapAsFlattenedKeyValueItems()
    {
        var buffer = SequenceHelpers.Single("%2\r\n+key1\r\n:1\r\n+key2\r\n:2\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Map, result.Type);

        var items = result.AsItems();
        Assert.Equal(4, items.Length);
        Assert.Equal("key1", items[0].AsString());
        Assert.Equal(1, items[1].AsInt64());
        Assert.Equal("key2", items[2].AsString());
        Assert.Equal(2, items[3].AsInt64());
    }

    [Fact]
    public void ParsesSet()
    {
        var buffer = SequenceHelpers.Single("~2\r\n:1\r\n:2\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Set, result.Type);
        Assert.Equal(2, result.AsItems().Length);
    }

    [Fact]
    public void ParsesPush()
    {
        var buffer = SequenceHelpers.Single(">2\r\n+pubsub\r\n+message\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal(RespType.Push, result.Type);
        Assert.Equal(2, result.AsItems().Length);
    }

    [Fact]
    public void IncompleteFrameReturnsFalseAndLeavesBufferUntouched()
    {
        var bytes = "$5\r\nhel"u8.ToArray();
        var buffer = SequenceHelpers.Single(bytes);
        var originalLength = buffer.Length;

        Assert.False(RespFrameReader.TryParse(ref buffer, out _));
        Assert.Equal(originalLength, buffer.Length);
    }

    [Fact]
    public void FrameCompletesOnceRemainingBytesArrive()
    {
        var partial = "$5\r\nhel"u8.ToArray();
        var buffer = SequenceHelpers.Single(partial);
        Assert.False(RespFrameReader.TryParse(ref buffer, out _));

        // Simulate the read loop appending newly-arrived bytes and retrying from scratch.
        var complete = "$5\r\nhello\r\n"u8.ToArray();
        buffer = SequenceHelpers.Single(complete);

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void ParsesAcrossSegmentBoundaryMidPayload()
    {
        // "$5\r\nhe" | "llo\r\n" — split inside the bulk string payload itself.
        var buffer = SequenceHelpers.Segmented("$5\r\nhe"u8.ToArray(), "llo\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal("hello", result.AsString());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void ParsesAcrossSegmentBoundaryMidLineTerminator()
    {
        // "+OK\r" | "\n" — split between the CR and the LF of the line terminator.
        var buffer = SequenceHelpers.Segmented("+OK\r"u8.ToArray(), "\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var result));
        Assert.Equal("OK", result.AsString());
    }

    [Fact]
    public void LeavesTrailingBytesForTheNextFrameOnPipelinedReplies()
    {
        var buffer = SequenceHelpers.Single("+OK\r\n:42\r\n"u8.ToArray());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var first));
        Assert.Equal("OK", first.AsString());

        Assert.True(RespFrameReader.TryParse(ref buffer, out var second));
        Assert.Equal(42, second.AsInt64());
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void ThrowsOnUnrecognizedTypePrefix()
    {
        var buffer = SequenceHelpers.Single("@nope\r\n"u8.ToArray());

        Assert.Throws<RespProtocolException>(() => RespFrameReader.TryParse(ref buffer, out _));
    }

    [Fact]
    public void ThrowsOnMalformedInteger()
    {
        var buffer = SequenceHelpers.Single(":not-a-number\r\n"u8.ToArray());

        Assert.Throws<RespProtocolException>(() => RespFrameReader.TryParse(ref buffer, out _));
    }

    [Fact]
    public void ThrowsOnInvalidNegativeBulkLength()
    {
        var buffer = SequenceHelpers.Single("$-2\r\n"u8.ToArray());

        Assert.Throws<RespProtocolException>(() => RespFrameReader.TryParse(ref buffer, out _));
    }
}
