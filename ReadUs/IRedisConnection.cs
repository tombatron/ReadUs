using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisConnection : IDisposable
    {
        bool IsConnected { get; }

        byte[] SendCommand(RedisCommandEnvelope command);

        Task<byte[]> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default);

        void Connect();

        Task ConnectAsync(CancellationToken cancellationToken = default);
    }
}
