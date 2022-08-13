using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisConnection : IDisposable
    {
        bool IsConnected { get; }

        Task<byte[]> SendCommandAsync(byte[] command);

        Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout);

        Task<byte[]> SendCommandAsync(byte[] command, CancellationToken cancellation);

        Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken);

        void Connect();

        Task ConnectAsync();
    }
}
