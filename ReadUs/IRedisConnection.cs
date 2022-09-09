using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisConnection : IDisposable
    {
        bool IsConnected { get; }

        void Connect();

        Task ConnectAsync(CancellationToken cancellationToken = default);

        byte[] SendCommand(RedisCommandEnvelope command);

        Task<byte[]> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default);
    }
}