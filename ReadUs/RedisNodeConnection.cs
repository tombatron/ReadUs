using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public sealed class RedisNodeConnection : IRedisNodeConnection
    {
        private readonly RedisConnection _backingConnection;

        public bool IsConnected => _backingConnection.IsConnected;


        public RedisNodeConnection(ClusterNodesResultItem nodeDescription)
        {
            // TODO: For now we're just going to assume that the data returned by the cluster nodes command
            //       is valid. Though we are going to want to throw in some logic to account for shifting
            //       roles, additional nodes, changing slot assignments etc.

            _backingConnection = new RedisConnection(nodeDescription.Address.IpAddress, nodeDescription.Address.RedisPort);

            
        }

        public ClusterNodeRole Role { get; }

        public ClusterSlots Slots { get; }

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