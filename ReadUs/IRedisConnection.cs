using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisConnection : IDisposable
    {
        bool IsConnected { get; }

        byte[] SendCommand(RedisKey key, byte[] command, TimeSpan timeout);

        byte[] SendCommand(RedisKey[] keys, byte[] command, TimeSpan timeout);

        byte[] SendCommand(byte[] command, TimeSpan timeout);

        Task<byte[]> SendCommandAsync(RedisKey key, byte[] command);

        Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command);

        Task<byte[]> SendCommandAsync(RedisKey key, byte[] command, TimeSpan timeout);

        Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command, TimeSpan timeout);

        Task<byte[]> SendCommandAsync(RedisKey key, byte[] command, CancellationToken cancellationToken);

        Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command, CancellationToken cancellationToken);

        Task<byte[]> SendCommandAsync(RedisKey key, byte[] command, TimeSpan timeout, CancellationToken cancellationToken);

        Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command, TimeSpan timeout, CancellationToken cancellationToken);

        Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken = default);

        void Connect();

        Task ConnectAsync(CancellationToken cancellationToken = default);
    }
}
