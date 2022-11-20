using System.Linq;
using ReadUs.ResultModels;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.Parser.Parser;
using static ReadUs.StandardValues;

namespace ReadUs;

// TODO: Let's put in the ability to name connections...
public class RedisConnection : IRedisConnection
{
    public IPEndPoint EndPoint { get; }

    private readonly Socket _socket;
    private readonly TimeSpan _commandTimeout;
    private readonly SemaphoreSlim _semaphore;

    private static int _connectionCount = 0;

    public string ConnectionName { get; }

    public RedisConnection(RedisConnectionConfiguration configuration) :
        this(configuration.ServerAddress, configuration.ServerPort)
    { }

    public RedisConnection(string address, int port) :
                this(address, port, TimeSpan.FromSeconds(30))
    { }

    public RedisConnection(string address, int port, TimeSpan commandTimeout) :
                this(ResolveIpAddress(address), port, commandTimeout)
    { }

    public RedisConnection(IPAddress address, int port) :
                this(address, port, TimeSpan.FromSeconds(30))
    { }

    public RedisConnection(IPAddress address, int port, TimeSpan commandTimeout) :
                this(new IPEndPoint(address, port), commandTimeout)
    { }

    public RedisConnection(IPEndPoint endPoint) :
                this(endPoint, TimeSpan.FromSeconds(30))
    { }

    public RedisConnection(IPEndPoint endPoint, TimeSpan commandTimeout)
    {
        EndPoint = endPoint;
        _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _commandTimeout = commandTimeout;
        _semaphore = new SemaphoreSlim(1, 1);

        ConnectionName = $"ReadUs_Connection_{++_connectionCount}";
    }

    public bool IsConnected => _socket.Connected;

    public bool IsBusy => _semaphore.CurrentCount == 0; // (* ￣︿￣)

    public void Connect()
    {
        Trace.WriteLine($"Connected {ConnectionName} to {EndPoint.Address}:{EndPoint.Port}.");

        _socket.Connect(EndPoint);

        SetConnectionClientName();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _socket.ConnectAsync(EndPoint);

        Trace.WriteLine($"Connected {ConnectionName} to {EndPoint.Address}:{EndPoint.Port}.");

        await SetConnectionClientNameAsync(cancellationToken);
    }

    private static readonly byte[] RoleCommandBytes = new byte[6] { 82, 79, 76, 69, 13, 10 };

    public RoleResult Role()
    {
        if (IsConnected)
        {
            var rawResult = SendCommand(RoleCommandBytes);

            var result = Parse(rawResult);

            return (RoleResult)result;
        }
        else
        {
            // TODO: Need a custom exception here.
            throw new Exception("Socket isn't ready can't execute command.");
        }
    }

    public async Task<RoleResult> RoleAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            var rawResult = await SendCommandAsync(RoleCommandBytes, TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            var result = Parse(rawResult);

            return (RoleResult)result;
        }
        else
        {
            // TODO: Need a custom exception here.
            throw new Exception("Socket isn't ready can't execute command.");
        }
    }

    public byte[] SendCommand(RedisCommandEnvelope command) =>
        SendCommand(command.ToByteArray());

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
        else
        {
            // TODO: This better...
            throw new Exception("I guess we didn't get anything...");
        }
    }

    public Task<byte[]> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken) =>
        SendCommandAsync(command.ToByteArray(), command.Timeout);
    public async Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync();

        var pipe = new Pipe();

        await _socket.SendAsync(command, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var buffer = pipe.Writer.GetMemory(512);
            int bytesReceived = default;

            async Task ReceiveBytes()
            {
                bytesReceived = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }

            var timeoutTask = Task.Delay(timeout, cancellationToken);

            if (await Task.WhenAny(timeoutTask, ReceiveBytes()) == timeoutTask)
            {
                // TODO: Custom exception here. 
                throw new Exception("Timeout expired.");
            }

            if (bytesReceived == 0)
            {
                await Task.Delay(10, cancellationToken);
            }
            else
            {
                pipe.Writer.Advance(bytesReceived);

                if (IsResponseComplete(bytesReceived, buffer.Span))
                {
                    await pipe.Writer.CompleteAsync();

                    break;
                }
            }
        }

        var responseResult = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var socketResult = responseResult.Buffer.ToArray();

        await pipe.Reader.CompleteAsync().ConfigureAwait(false);

        _semaphore.Release();

        return socketResult;
    }

    public void Dispose()
    {
        _socket.Close();
        _socket.Dispose();

        Trace.WriteLine($"Connection {ConnectionName} ({EndPoint.Address}:{EndPoint.Port}) disposed.");
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
            if (parsedMembers == (length - 1) && contentSlice.Length == 0)
            {
                // We're here, it looks like we're on the final item in the array and there is
                // no more data in the buffer...
                return (true, start);
            }

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
        {
            // Looks like we were right, let's return the parsed IP address.
            return ipAddress;
        }

        // OK, it looks like it wasn't an IP address, let's see if we can assume this is a 
        // host name... This will throw if the provided address does not resolve.
        var resolvedAddresses = Dns.GetHostAddresses(address);

        // I'm not really sure what to do with multiple results yet so we're just going to
        // pick the first one. 
        return resolvedAddresses[0];
    }

    private void SetConnectionClientName() =>
        SendCommand(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName));

    private Task SetConnectionClientNameAsync(CancellationToken cancellationToken) =>
        SendCommandAsync(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName), cancellationToken);
}
