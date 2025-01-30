using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Exceptions;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;
using static ReadUs.StandardValues;

namespace ReadUs;

// TODO: Let's put in the ability to name connections...
public class RedisConnection : IRedisConnection
{
    private static int _connectionCount;

    private static readonly byte[] RoleCommandBytes = "ROLE\r\n"u8.ToArray();
    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _semaphore;

    private readonly Socket _socket;

    public RedisConnection(RedisConnectionConfiguration configuration) :
        this(configuration.ServerAddress, configuration.ServerPort)
    {
    }

    public RedisConnection(string address, int port) :
        this(address, port, TimeSpan.FromSeconds(30))
    {
    }

    public RedisConnection(string address, int port, TimeSpan commandTimeout) :
        this(ResolveIpAddress(address), port, commandTimeout)
    {
    }

    public RedisConnection(IPAddress address, int port) :
        this(address, port, TimeSpan.FromSeconds(30))
    {
    }

    public RedisConnection(IPAddress address, int port, TimeSpan commandTimeout) :
        this(new IPEndPoint(address, port), commandTimeout)
    {
    }

    public RedisConnection(IPEndPoint endPoint) :
        this(endPoint, TimeSpan.FromSeconds(30))
    {
    }

    public RedisConnection(IPEndPoint endPoint, TimeSpan commandTimeout)
    {
        EndPoint = endPoint;
        _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _commandTimeout = commandTimeout;
        _semaphore = new SemaphoreSlim(1, 1);

        ConnectionName = $"ReadUs_Connection_{++_connectionCount}";
    }

    public IPEndPoint EndPoint { get; }

    public string ConnectionName { get; }

    public bool IsBusy => _semaphore.CurrentCount == 0; // (* ￣︿￣) ???

    public bool IsConnected => _socket.Connected;

    public void Connect()
    {
        Trace.WriteLine($"Connected {ConnectionName} to {EndPoint.Address}:{EndPoint.Port}.");

        _socket.Connect(EndPoint);

        SetConnectionClientName();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        cancellationTimeout.CancelAfter(_commandTimeout);

        try
        {
            await _socket.ConnectAsync(EndPoint, cancellationTimeout.Token).ConfigureAwait(false);

            Trace.WriteLine($"Connected {ConnectionName} to {EndPoint.Address}:{EndPoint.Port}.");

            await SetConnectionClientNameAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // We're in here now assuming that the cancellation is because the timeout has lapsed.
            Trace.WriteLine("Connection attempt timed out.");
            
            throw;
        }
    }

    public RoleResult Role()
    {
        if (IsConnected)
        {
            var rawResult = SendCommand(RoleCommandBytes);

            var result = Parse(rawResult).Unwrap();

            return (RoleResult)result;
        }
        
        throw new RedisConnectionException("Socket isn't ready can't execute command.");
    }

    public async Task<RoleResult> RoleAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            var rawResult = await SendCommandAsync(RoleCommandBytes, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            var result = Parse(rawResult).Unwrap();

            return (RoleResult)result;
        }
        
        throw new RedisConnectionException("Socket isn't ready can't execute command.");
    }

    public byte[] SendCommand(RedisCommandEnvelope command) => SendCommand(command.ToByteArray());

    public Task<byte[]> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken) => 
        SendCommandAsync(command.ToByteArray(), command.Timeout, cancellationToken);

    public void Dispose()
    {
        _socket.Close();
        _socket.Dispose();

        Trace.WriteLine($"Connection {ConnectionName} ({EndPoint.Address}:{EndPoint.Port}) disposed.");
    }

    public byte[] SendCommand(byte[] command)
    {
        _semaphore.Wait();

        var pipe = new Pipe();

        _socket.Send(command, SocketFlags.None);

        while (true)
        {
            var buffer = pipe.Writer.GetMemory(512);
            int bytesReceived = default;

            try
            {
                bytesReceived = _socket.Receive(buffer.Span, SocketFlags.None);
            }
            catch (SocketException ex)
            {
                throw new Exception("Command timeout expired.", ex);
            }

            if (bytesReceived == 0)
            {
                Thread.Sleep(10); // There's gotta be a better way I guess?
            }
            else
            {
                pipe.Writer.Advance(bytesReceived);

                if (IsResponseComplete(bytesReceived, buffer.Span))
                {
                    pipe.Writer.Complete();

                    break;
                }
            }
        }

        if (pipe.Reader.TryRead(out var readResult))
        {
            var result = readResult.Buffer.ToArray();

            _semaphore.Release();

            return result;
        }

        // TODO: This better...
        throw new Exception("I guess we didn't get anything...");
    }

    public async Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cancellationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationTimeout.CancelAfter(timeout);
        
        var pipe = new Pipe();
        byte[]? socketResult = null;
        
        try
        {
            await _semaphore.WaitAsync(cancellationTimeout.Token);
            
            await _socket.SendAsync(command, SocketFlags.None, cancellationTimeout.Token).ConfigureAwait(false);

            while (true)
            {
                var memory = pipe.Writer.GetMemory(512);
                
                var bytesReceived = await _socket.ReceiveAsync(memory, SocketFlags.None, cancellationTimeout.Token).ConfigureAwait(false);

                if (bytesReceived == 0)
                {
                    await Task.Delay(10, cancellationToken);
                }
                else
                {
                    pipe.Writer.Advance(bytesReceived);
                    
                    var flushResult = await pipe.Writer.FlushAsync(cancellationTimeout.Token).ConfigureAwait(false);

                    if (flushResult.IsCompleted || flushResult.IsCanceled)
                    {
                        break;
                    }
                    
                    var readResult = await pipe.Reader.ReadAsync(cancellationTimeout.Token).ConfigureAwait(false);
                    
                    var buffer = readResult.Buffer;
                    
                    if (IsResponseComplete(buffer))
                    {
                        await pipe.Writer.CompleteAsync();
                        
                        socketResult = buffer.ToArray();

                        break;
                    }
                }
            }
        }
        finally
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);

            _semaphore.Release();
        }

        if (socketResult is null)
        {
            // Pretty sure that Redis commands should always return something...
            throw new Exception("I guess we didn't get anything...");
        }

        return socketResult;
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

        if (!reader.TryReadTo(out ReadOnlySpan<byte> lengthBytes, CarriageReturnLineFeed.AsSpan(),
                advancePastDelimiter: true))
        {
            return false;
        }

        var lengthString = Encoding.ASCII.GetString(lengthBytes)[1..]; // The first character would be a *.

        if (!int.TryParse(lengthString, out var length))
        {
            throw new InvalidOperationException("Unable to parse array length.");
        }
        
        var start = HeaderTokenLength + lengthBytes.Length + CrlfLength;

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

    private bool IsResponseComplete(int bytesReceived, Span<byte> buffer)
    {
        var (isComplete, _) = IsResponseComplete(buffer.Slice(0, bytesReceived));

        return isComplete;
    }

    private static (bool, int) IsResponseComplete(Span<byte> buffer)
    {
        switch (buffer[0])
        {
            case SimpleStringHeader:
            case ErrorHeader:
            case IntegerHeader:
                // Simple strings, errors, and integers are all "simple values" therefore we can handle them 
                // all the same when verifying that we've received everything. 

                // Here we're looking at the last four populated bytes of the buffer and checking to see if they
                // are a CRLF. 
                var endLocation = buffer.IndexOf(CarriageReturnLineFeed) + 2;
                var isComplete = buffer.IndexOf(CarriageReturnLineFeed) > -1;

                return (isComplete, endLocation);
            case BulkStringHeader:
                return IsCompleteBulkString(buffer);
            case ArrayHeader:
                return IsArrayComplete(buffer);
            default:
                throw new Exception("(╯°□°）╯︵ ┻━┻");
        }
    }

    private static (bool, int) IsCompleteBulkString(Span<byte> buffer)
    {
        var firstCrlf = buffer.IndexOf(CarriageReturnLineFeed);
        var lengthBytes = buffer.Slice(1, firstCrlf - 1);
        var lengthString = Encoding.ASCII.GetString(lengthBytes); // TODO: Maybe there's a better way to do this?

        var length = int.Parse(lengthString);
        var end = HeaderTokenLength + lengthString.Length + CrlfLength + length;

        var isComplete = buffer.Slice(end, 2).IndexOf(CarriageReturnLineFeed) > -1;
        var totalLength = end + 2;

        return (isComplete, totalLength);
    }

    private static (bool, int) IsArrayComplete(Span<byte> buffer)
    {
        var firstCrlf = buffer.IndexOf(CarriageReturnLineFeed);
        var lengthBytes = buffer.Slice(1, firstCrlf - 1);
        var lengthString = Encoding.ASCII.GetString(lengthBytes);

        var length = int.Parse(lengthString);
        var start = HeaderTokenLength + lengthBytes.Length + CrlfLength;

        var parsedMembers = 0;

        for (var i = 0; i < length; i++)
        {
            var contentSlice = buffer.Slice(start);

            // Are we checking the final item in the response? If so, it's possible that the
            // item will be nil... I suppose it's possible that the response hasn't fully
            // made it to us but uh... not sure at this point a way around this...
            if (parsedMembers == length - 1 && contentSlice.Length == 0)
                // We're here, it looks like we're on the final item in the array and there is
                // no more data in the buffer...
                return (true, start);

            var (complete, resultLength) = IsResponseComplete(contentSlice);

            if (complete)
            {
                parsedMembers++;

                start += resultLength;
            }
            else
            {
                return default;
            }
        }

        return (parsedMembers == length, start);
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

    private void SetConnectionClientName()
    {
        SendCommand(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName));
    }

    private Task SetConnectionClientNameAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName), cancellationToken);
    }
}