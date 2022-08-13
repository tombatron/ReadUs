using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.StandardValues;

namespace ReadUs
{
    // TODO: Let's put in the ability to name connections...
    public class RedisConnection : IRedisConnection
    {
        private readonly IPEndPoint _endPoint;
        private readonly Socket _socket;
        private readonly TimeSpan _commandTimeout;

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
            _endPoint = endPoint;
            _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _commandTimeout = commandTimeout;
        }

        public bool IsConnected { get; private set; }

        public void Connect()
        {
            _socket.Connect(_endPoint);

            IsConnected = true;
        }

        public async Task ConnectAsync()
        {
            await _socket.ConnectAsync(_endPoint);

            IsConnected = true;
        }

        public byte[] SendCommand(byte[] command, TimeSpan timeout)
        {
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
                return readResult.Buffer.ToArray();
            }
            else
            {
                // TODO: This better...
                throw new Exception("I guess we didn't get anything...");
            }
        }

        public Task<byte[]> SendCommandAsync(byte[] command) =>
            SendCommandAsync(command, _commandTimeout);

        public Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout) =>
            SendCommandAsync(command, timeout, CancellationToken.None);

        public Task<byte[]> SendCommandAsync(byte[] command, CancellationToken cancellationToken) =>
            SendCommandAsync(command, _commandTimeout, cancellationToken);

        public async Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken)
        {
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
                
                if(await Task.WhenAny(timeoutTask, ReceiveBytes()) == timeoutTask)
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

            return socketResult;
        }

        public void Dispose()
        {
            _socket.Close();
            _socket.Dispose();
        }

        private int _totalBytes = 0;

        private bool IsResponseComplete(int bytesReceived, Span<byte> buffer)
        {
            var currentTotalBytes = _totalBytes + bytesReceived;

            bool isComplete;

            switch (buffer[0])
            {
                case SimpleStringHeader:
                case ErrorHeader:
                case IntegerHeader:
                    isComplete = buffer.Slice(currentTotalBytes - 4, 4).IndexOf(CarriageReturnLineFeed) > -1;
                    break;
                case BulkStringHeader:
                    (isComplete, _) = IsCompleteBulkString(buffer);
                    break;
                case ArrayHeader:
                    isComplete = IsArrayComplete(buffer);
                    break;
                default:
                    throw new Exception("(╯°□°）╯︵ ┻━┻");
            }

            if (isComplete)
            {
                _totalBytes = 0;
            }

            return isComplete;
        }

        private static (bool, int) IsCompleteBulkString(Span<byte> buffer)
        {
            var firstCrlf = buffer.IndexOf(CarriageReturnLineFeed);
            var lengthBytes = buffer.Slice(1, firstCrlf - 1);
            var lengthString = Encoding.ASCII.GetString(lengthBytes);

            var length = int.Parse(lengthString);
            var end = HeaderTokenLength + lengthString.Length + CrlfLength + length;

            var isComplete = buffer.Slice(end, 2).IndexOf(CarriageReturnLineFeed) > -1;
            var totalLength = end + 2;

            return (isComplete, totalLength);
        }

        private static bool IsArrayComplete(Span<byte> buffer)
        {
            var firstCrlf = buffer.IndexOf(CarriageReturnLineFeed);
            var lengthBytes = buffer.Slice(1, firstCrlf - 1);
            var lengthString = Encoding.ASCII.GetString(lengthBytes);

            var length = int.Parse(lengthString);
            var start = HeaderTokenLength + lengthBytes.Length + CrlfLength;

            for (var i = 0; i < length; i++)
            {
                var (complete, resultLength) = IsCompleteBulkString(buffer.Slice(start));

                if (complete)
                {
                    start += resultLength;
                }
                else
                {
                    return false;
                }
            }

            return true;
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
    }
}