using System.Buffers;
using System.Text;
using ReadUs.Protocol;

namespace ReadUs.Tests.Unit.Protocol;

public class RespCommandWriterTests
{
    [Fact]
    public void WritesCommandWithNoArguments()
    {
        var buffer = new ArrayBufferWriter<byte>();

        RespCommandWriter.WriteCommand(buffer, "PING"u8, []);

        Assert.Equal("*1\r\n$4\r\nPING\r\n", Encoding.ASCII.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void WritesCommandWithArguments()
    {
        var buffer = new ArrayBufferWriter<byte>();
        ReadOnlyMemory<byte>[] args = ["mykey"u8.ToArray(), "myvalue"u8.ToArray()];

        RespCommandWriter.WriteCommand(buffer, "SET"u8, args);

        Assert.Equal(
            "*3\r\n$3\r\nSET\r\n$5\r\nmykey\r\n$7\r\nmyvalue\r\n",
            Encoding.ASCII.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void WritesEmptyBulkStringArgument()
    {
        var buffer = new ArrayBufferWriter<byte>();
        ReadOnlyMemory<byte>[] args = [ReadOnlyMemory<byte>.Empty];

        RespCommandWriter.WriteCommand(buffer, "ECHO"u8, args);

        Assert.Equal("*2\r\n$4\r\nECHO\r\n$0\r\n\r\n", Encoding.ASCII.GetString(buffer.WrittenSpan));
    }

    [Fact]
    public void RoundTripsThroughTheFrameReader()
    {
        var buffer = new ArrayBufferWriter<byte>();
        ReadOnlyMemory<byte>[] args = ["k"u8.ToArray(), "v"u8.ToArray()];
        RespCommandWriter.WriteCommand(buffer, "SET"u8, args);

        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        Assert.True(RespFrameReader.TryParse(ref sequence, out var result));

        var items = result.AsItems();
        Assert.Equal(3, items.Length);
        Assert.Equal("SET", items[0].AsString());
        Assert.Equal("k", items[1].AsString());
        Assert.Equal("v", items[2].AsString());
    }
}
