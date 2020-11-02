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
    public sealed class ReadUsConnection : IReadUsConnection
    {
        private readonly IPEndPoint _endPoint;
        private readonly Socket _socket;
        private readonly TimeSpan _commandTimeout;

        public ReadUsConnection(string address, int port) :
            this(address, port, TimeSpan.FromSeconds(30))
        { }

        public ReadUsConnection(string address, int port, TimeSpan commandTimeout) :
            this(IPAddress.Parse(address), port, commandTimeout)
        { }

        public ReadUsConnection(IPAddress address, int port) :
            this(address, port, TimeSpan.FromSeconds(30))
        { }

        public ReadUsConnection(IPAddress address, int port, TimeSpan commandTimeout) :
            this(new IPEndPoint(address, port), commandTimeout)
        { }

        public ReadUsConnection(IPEndPoint endPoint) :
            this(endPoint, TimeSpan.FromSeconds(30))
        { }

        public ReadUsConnection(IPEndPoint endPoint, TimeSpan commandTimeout)
        {
            _endPoint = endPoint;
            _socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _commandTimeout = commandTimeout;
        }

        public bool IsConnected { get; private set; }

        public async Task ConnectAsync()
        {
            await _socket.ConnectAsync(_endPoint);

            IsConnected = true;
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

                var timeoutTask = Task.Delay(timeout);
                
                if(await Task.WhenAny(timeoutTask, ReceiveBytes()) == timeoutTask)
                {
                    throw new Exception("Timeout expired.");
                }

                if (bytesReceived == 0)
                {
                    await Task.Delay(10);
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
    }
}