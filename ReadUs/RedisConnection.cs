using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;

using static ReadUs.StandardValues;

namespace ReadUs;

public partial class RedisConnection : IRedisConnection
{
    private const int MinimumBufferSize = 512;

    private static int _connectionCount;

    private readonly IPEndPoint _endPoint;
    private readonly Socket _socket;
    private readonly TimeSpan _commandTimeout;

    // TODO: Going to rename this at some point. 
    private Task _backgroundTask;
    private readonly CancellationTokenSource _backgroundTaskCancellationTokenSource = new();
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(1);

    public IPEndPoint EndPoint => _endPoint;

    public RedisConnection(RedisConnectionConfiguration configuration) :
        this(configuration.ServerAddress, configuration.ServerPort)
    {
    }

    public RedisConnection(string address, int port) :
        this(ResolveIpAddress(address), port)
    {
    }

    public RedisConnection(IPAddress ipAddress, int port) :
        this(new IPEndPoint(ipAddress, port), TimeSpan.FromSeconds(30))
    {
    }

    public RedisConnection(IPEndPoint endPoint, TimeSpan commandTimeout)
    {
        _endPoint = endPoint;
        _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _commandTimeout = commandTimeout;
    }

    public string ConnectionName { get; } = $"ReadUs_Connection_{++_connectionCount}";

    public bool IsConnected => _socket.Connected;
    
    private static async Task ConnectionWorker(Channel<byte[]> channel, Socket socket, RedisConnection @this, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new Pipe();
            
            var writer = pipe.Writer;
            var reader = pipe.Reader;

            var fillTask = FillPipe(socket, writer, cancellationToken);
            var readTask = ReadPipe(reader, channel, cancellationToken);

            await Task.WhenAll(fillTask, readTask);
        }
    }

    private static async Task FillPipe(Socket socket, PipeWriter writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var memory = writer.GetMemory(MinimumBufferSize);

            var bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);

            if (bytesRead == 0)
            {
                break; // We think the connection is closed...
            }

            writer.Advance(bytesRead);

            var result = await writer.FlushAsync(cancellationToken);

            if (result.IsCompleted)
            {
                break;
            }
        }

        await writer.CompleteAsync();
    }

    private static async Task ReadPipe(PipeReader reader, Channel<byte[]> channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(cancellationToken);

            var buffer = result.Buffer;

            if (IsResponseComplete(buffer, out var consumedLength))
            {
                // Slice off the data that we've consumed and return.
                var responseData = buffer.Slice(0, consumedLength).ToArray();
                
                await channel.Writer.WriteAsync(responseData, cancellationToken);
                
                reader.AdvanceTo(buffer.GetPosition(consumedLength));
            }
            else
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }
            
            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
    }
    
    public void Dispose()
    {
        _backgroundTaskCancellationTokenSource.Cancel();

        _socket.Close();
        _socket.Dispose();
    }

    private static bool IsResponseComplete(ReadOnlySequence<byte> buffer, out long consumedLength)
    {
        if (buffer.Length == 0)
        {
            consumedLength = 0;
            return false;
        }

        var firstByte = buffer.FirstSpan[0];

        switch (firstByte)
        {
            case SimpleStringHeader:
            case ErrorHeader:
            case IntegerHeader:
                return EndsWithCrLf(buffer, out consumedLength);
            case BulkStringHeader:
                return IsCompleteBulkString(buffer, out consumedLength);
            case ArrayHeader:
                return IsArrayComplete(buffer, out consumedLength);
            default:
                throw new Exception("(╯°□°）╯︵ ┻━┻");
        }
    }

    private static readonly byte[] CarriageReturnLineFeed = "\r\n"u8.ToArray();

    private static bool EndsWithCrLf(ReadOnlySequence<byte> buffer, out long consumedLength)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (reader.Remaining < 2)
        {
            // If the buffer is less than 2 bytes long, it's not possible for it to end with a CRLF.
            consumedLength = 0;
            return false;
        }

        if (!reader.TryReadTo(out ReadOnlySpan<byte> _, CarriageReturnLineFeed.AsSpan(), advancePastDelimiter: true))
        {
            // If we can't find the first byte of the CRLF, it's not possible for it to end with a CRLF.
            consumedLength = 0;
            return false;
        }

        consumedLength = reader.Consumed;
        return true;
    }

    private static bool IsCompleteBulkString(ReadOnlySequence<byte> buffer, out long consumedLength)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (reader.Remaining < 2)
        {
            // If the buffer is less than 2 bytes long, it's not possible for it to be a bulk string.
            consumedLength = 0;
            return false;
        }

        if (!reader.TryReadTo(out ReadOnlySpan<byte> lengthBytes, CarriageReturnLineFeed.AsSpan(), advancePastDelimiter: true))
        {
            consumedLength = 0;
            return false;
        }

        var lengthString = Encoding.ASCII.GetString(lengthBytes)[1..]; // The first character would be a $.

        if (!int.TryParse(lengthString, out var length))
        {
            throw new InvalidOperationException("Unable to parse bulk string length.");
        }

        var expectedLength = length + CrlfLength; // We've already read past the initial header. 

        if (reader.Remaining < expectedLength)
        {
            consumedLength = 0;
            return false;
        }

        reader.Advance(expectedLength);

        consumedLength = reader.Consumed;
        return true;
    }

    private static bool IsArrayComplete(ReadOnlySequence<byte> buffer, out long consumedLength)
    {
        var reader = new SequenceReader<byte>(buffer);

        // Make sure we have something to work with.
        if (reader.Remaining < 2)
        {
            consumedLength = 0;
            return false;
        }

        // Skip the type header...
        reader.Advance(1);

        // Read the length of the array.
        if (!reader.TryReadTo(out ReadOnlySpan<byte> lengthBytes, CarriageReturnLineFeed.AsSpan(), advancePastDelimiter: true))
        {
            consumedLength = 0;
            return false;
        }

        // Parse the length...
        if (!int.TryParse(Encoding.ASCII.GetString(lengthBytes), out var length))
        {
            consumedLength = 0;
            return false;
        }

        // It's possible that the array is empty...
        if (length == 0)
        {
            consumedLength = reader.Consumed;
            return true;
        }

        for (var i = 0; i < length; i++)
        {
            if (reader.Remaining == 0)
            {
                // Not enough data
                consumedLength = 0;
                return false;
            }

            // Peak at the next type character...
            reader.TryPeek(out var type);

            bool elementComplete = false;
            long elementLength;

            switch (type)
            {
                case SimpleStringHeader:
                case ErrorHeader:
                case IntegerHeader:
                    elementComplete = EndsWithCrLf(reader.UnreadSequence, out elementLength);
                    break;
                case BulkStringHeader:
                    elementComplete = IsCompleteBulkString(reader.UnreadSequence, out elementLength);
                    break;
                case ArrayHeader:
                    elementComplete = IsArrayComplete(reader.UnreadSequence, out elementLength);
                    break;
                default:
                    // Whatever...
                    consumedLength = 0;
                    return false;
            }

            if (!elementComplete)
            {
                consumedLength = 0;
                return false;
            }

            reader.Advance(elementLength);
        }

        consumedLength = reader.Consumed;

        return true;
    }

    private static IPAddress ResolveIpAddress(string address)
    {
        // We're starting with a string. We're assuming that the string is an IP address but
        // just in case let's check. 
        if (IPAddress.TryParse(address, out var ipAddress))
            // Looks like we were right, let's return the parsed IP address.
            return ipAddress;

        // OK, it looks like it wasn't an IP address, let's see if we can assume this is a 
        // host name... This will throw if the provided address does not resolve.
        var resolvedAddresses = Dns.GetHostAddresses(address);

        // I'm not really sure what to do with multiple results yet so we're just going to
        // pick the first one. 
        return resolvedAddresses[0];
    }
}