using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;
using static ReadUs.StandardValues;

namespace ReadUs;

public class RedisConnection : IRedisConnection
{
    private const int MinimumBufferSize = 512;
    
    private static int _connectionCount = 0;

    private readonly IPEndPoint _endPoint;
    private readonly Socket _socket;
    private readonly TimeSpan _commandTimeout;

    // TODO: Going to rename this at some point. 
    private Task _backgroundTask;
    private CancellationTokenSource _backgroundTaskCancellationTokenSource = new();
    private readonly Channel<Result<byte[]>> _channel = Channel.CreateBounded<Result<byte[]>>(1);

    public IPEndPoint EndPoint => _endPoint;

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

    public void Connect()
    {
        _socket.Connect(_endPoint);
        
        Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

        _backgroundTask = Task.Run(() => ConnectionWorker(_channel, _socket, this, CancellationToken.None), _backgroundTaskCancellationTokenSource.Token);

        SetConnectionClientName();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cancellationTimeout.CancelAfter(_commandTimeout);

        try
        {
            await _socket.ConnectAsync(_endPoint, cancellationTimeout.Token).ConfigureAwait(false);

            Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

            _backgroundTask = Task.Run(() => ConnectionWorker(_channel, _socket, this, cancellationToken), _backgroundTaskCancellationTokenSource.Token);

            await SetConnectionClientNameAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // We're in here now assuming that the cancellation is because the timeout has lapsed.
            Trace.WriteLine("Connection attempt timed out.");

            throw;
        }
    }

    public Result<byte[]> SendCommand(RedisCommandEnvelope command)
    {
        // Write command. 
        _socket.Send(command.ToByteArray());

        // Read response. 
        var reader = _channel.Reader;

        // Wait until there is something to read. 
        // TODO: Probably need to think about a timeout here, but for now we'll just block without one. 
        reader.WaitToReadAsync().GetAwaiter().GetResult();

        if (reader.TryRead(out var response))
        {
            return response;
        }

        return Result<byte[]>.Error("Failed to read response.");
    }

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        // Write command.
        await _socket.SendAsync(command.ToByteArray(), cancellationToken);
        
        // Wait for a response.
        await _channel.Reader.WaitToReadAsync(cancellationToken);
        
        if (_channel.Reader.TryRead(out var response))
        {
            return response;
        }

        return Result<byte[]>.Error("Failed to read response.");
    }

    private static async Task ConnectionWorker(Channel<Result<byte[]>> channel, Socket socket, RedisConnection @this, CancellationToken cancellationToken)
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
    }

    private static async Task ReadPipe(PipeReader reader, Channel<Result<byte[]>> channel, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(cancellationToken);

            var buffer = result.Buffer;

            // TODO: This is probably super bad for performance gonna need to refactor this later.
            // I'm just trying to get this working. This is pretty inefficient because it's possible
            // that for bigger responses we'd be copying the buffer a lot.
            // Need something that can efficiently handle very large response. 
            var parseResult = Parse(buffer.ToArray().AsSpan());

            if (parseResult is Ok<ParseResult> ok)
            {
                await channel.Writer.WriteAsync(Result<byte[]>.Ok(buffer.ToArray()), cancellationToken);
            }

            if (parseResult is Error<ParseResult> err)
            {
                // Hrmmm...
            }
            
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
    }

    // Putting the `Role` and `RoleAsync` methods here to show that they are really only relevent
    // to a specific connection.    
    public Result<RoleResult> Role()
    {
        if (IsConnected)
        {
            var result = SendCommand(RedisCommandEnvelope.CreateRoleCommand());

            if (result is Ok<byte[]> ok)
            {
                var parseResult = Parse(ok.Value);

                if (parseResult is Ok<ParseResult> parseOk)
                {
                    return Result<RoleResult>.Ok((RoleResult)parseOk.Value);
                }
                
                if (parseResult is Error<ParseResult> parseErr)
                {
                    return Result<RoleResult>.Error(parseErr.Message);
                }
            }

            if (result is Error<byte[]> err)
            {
                return Result<RoleResult>.Error(err.Message);
            }
        }

        return Result<RoleResult>.Error("Socket isn't ready, can't execute command.");
    }

    public async Task<Result<RoleResult>> RoleAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            var result = await SendCommandAsync(RedisCommandEnvelope.CreateRoleCommand(), cancellationToken);

            if (result is Ok<byte[]> ok)
            {
                var parseResult = Parse(ok.Value);

                if (parseResult is Ok<ParseResult> parseOk)
                {
                    return Result<RoleResult>.Ok((RoleResult)parseOk.Value);
                }

                if (parseResult is Error<ParseResult> parseErr)
                {
                    return Result<RoleResult>.Error(parseErr.Message);
                }
            }

            if (result is Error<byte[]> err)
            {
                return Result<RoleResult>.Error(err.Message);
            }
        }
        
        return Result<RoleResult>.Error("Socket isn't ready, can't execute command.");
    }

    // Putting the `SetConnectionClientName` and `SetConnectionClientNameAsync` methods here to show that they are really only relevent
    // to a specific connection.    
    private void SetConnectionClientName() =>
        SendCommand(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName));

    private async Task SetConnectionClientNameAsync(CancellationToken cancellationToken) =>
        await SendCommandAsync(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName), cancellationToken);


    public void Dispose()
    {
        _backgroundTaskCancellationTokenSource.Cancel();
        
        _socket.Close();
        _socket.Dispose();
    }
    
    private static bool IsResponseComplete(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return false;
        }
        
        var firstByte = buffer.FirstSpan[0];
        
        switch (firstByte)
        {
            case SimpleStringHeader:
            case ErrorHeader:
            case IntegerHeader:
                return EndsWithCrLf(buffer);
            case BulkStringHeader:
                return IsCompleteBulkString(buffer);
            case ArrayHeader:
                return IsArrayComplete(buffer);
            default:
                throw new Exception("(╯°□°）╯︵ ┻━┻");
        }
    }
    
    private static readonly byte[] CarriageReturnLineFeed = "\r\n"u8.ToArray();
    private static bool EndsWithCrLf(ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length < 2)
        {
            return false;
        }
        
        var endSlice = buffer.Slice(buffer.Length - CarriageReturnLineFeed.Length, CarriageReturnLineFeed.Length );
        
        return endSlice.ToArray().AsSpan().SequenceEqual(CarriageReturnLineFeed);        
    }    
    
    private static bool IsCompleteBulkString(ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryReadTo(out ReadOnlySpan<byte> lengthBytes, CarriageReturnLineFeed.AsSpan(), advancePastDelimiter: false))
        {
            return false;
        }

        var lengthString = Encoding.ASCII.GetString(lengthBytes)[1..]; // The first character would be a $.

        if (!int.TryParse(lengthString, out var length))
        {
            throw new InvalidOperationException("Unable to parse bulk string length.");
        }
        
        var expectedLength = HeaderTokenLength + lengthString.Length + CrlfLength + length + CrlfLength;

        if (buffer.Length < expectedLength)
        {
            return false; 
        }
        
        var endSlice = buffer.Slice(expectedLength - CrlfLength, CrlfLength);
        
        var isComplete = endSlice.ToArray().SequenceEqual(CarriageReturnLineFeed);

        return isComplete;
    }
    
    private static bool IsArrayComplete(ReadOnlySequence<byte> buffer)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryReadTo(out ReadOnlySpan<byte> lengthBytes, CarriageReturnLineFeed.AsSpan(), advancePastDelimiter: true))
        {
            return false;
        }

        var lengthString = Encoding.ASCII.GetString(lengthBytes)[1..]; // The first character would be a *.

        if (!int.TryParse(lengthString, out var length))
        {
            throw new InvalidOperationException("Unable to parse array length.");
        }

        // TODO: I hacked this to make it work. I've gotta roll back and address this response handling because I don't
        //       remember how it works. (っ °Д °;)っ
        var start = HeaderTokenLength + lengthBytes.Length + 1;

        var parsedMembers = 0;

        for (var i = 0; i < length; i++)
        {
            var contentSlice = buffer.Slice(start);

            // Are we checking the final item in the response? If so, it's possible that the
            // item will be nil... I suppose it's possible that the response hasn't fully
            // made it to us but uh... not sure at this point a way around this...
            if (parsedMembers == length - 1 && contentSlice.Length == 0)
            {
                // We're here, it looks like we're on the final item in the array and there is
                // no more data in the buffer...
                return true;
            }
            
            var complete = IsResponseComplete(contentSlice.Slice(0, contentSlice.Length));

            if (complete)
            {
                parsedMembers++;
            }
            else
            {
                return false;
            }
        }

        return parsedMembers == length;
    }    
}