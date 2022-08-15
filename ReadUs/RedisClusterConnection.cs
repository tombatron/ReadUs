using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.RedisKeyUtilities;

namespace ReadUs
{
    // RedisClusterConnection is essentially just a collection of connections to the various
    // redis cluster nodes. 
    public class RedisClusterConnection : List<IRedisNodeConnection>, IDisposable
    {
        public RedisClusterConnection(ClusterNodesResult nodes) =>
            InitializeConnections(nodes);

        private void InitializeConnections(ClusterNodesResult nodes)
        {
            foreach (var node in nodes)
            {
                this.Add(new RedisNodeConnection(node));
            }
        }

        public bool IsConnected => this.All(x => x.IsConnected);

        public Task<byte[]> SendCommandAsync(string key, byte[] command) =>
            SendCommandAsync(ComputeHashSlot(key), command);

        private Task<byte[]> SendCommandAsync(int slot, byte[] command)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(string key, byte[] command, TimeSpan timeout) =>
            SendCommandAsync(ComputeHashSlot(key), command, timeout);


        private Task<byte[]> SendCommandAsync(int slot, byte[] command, TimeSpan timeout)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(string key, byte[] command, CancellationToken cancellation) =>
            SendCommandAsync(ComputeHashSlot(key), command, cancellation);

        private Task<byte[]> SendCommandAsync(int slot, byte[] command, CancellationToken cancellation)
        {
            throw new NotImplementedException();
        }

        public Task<byte[]> SendCommandAsync(string key, byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            SendCommandAsync(ComputeHashSlot(key), command, timeout, cancellationToken);


        private Task<byte[]> SendCommandAsync(int slot, byte[] command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private IRedisNodeConnection GetNodeForSlot(int slot) => 
            this.FirstOrDefault(x => x.Slots.ContainsSlot(slot));

        public void Connect()
        {
            foreach (var connection in this)
            {
                connection.Connect();
            }
        }

        public async Task ConnectAsync()
        {
            foreach (var connection in this)
            {
                await connection.ConnectAsync();
            }
        }

        public void Dispose()
        {
            foreach (var connection in this)
            {
                connection.Dispose();
            }
        }
    }
}