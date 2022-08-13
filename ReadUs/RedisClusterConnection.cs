using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public sealed class RedisClusterConnection : IRedisConnection
    {
        public bool IsConnected => throw new NotImplementedException();

        public void Connect()
        {
            
        }

        public Task ConnectAsync()
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(byte[] command)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(byte[] command, CancellationToken cancellation)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}