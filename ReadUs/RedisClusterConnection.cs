using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.RedisKeyUtilities;

namespace ReadUs
{
    // RedisClusterConnection is essentially just a collection of connections to the various
    // redis cluster nodes. 
    public class RedisClusterConnection : List<RedisNodeConnection>, IDisposable
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

        public Task<byte[]> SendCommandAsync(string[] keys, byte[] command) =>
            SendCommandAsync(ComputeHashSlot(keys), command);

        private Task<byte[]> SendCommandAsync(uint slot, byte[] command) =>
            GetNodeForSlot(slot).SendCommandAsync(command);

        public Task<byte[]> SendCommandAsync(string key, byte[] command, TimeSpan timeout) =>
            SendCommandAsync(ComputeHashSlot(key), command, timeout);

        public Task<byte[]> SendCommandAsync(string[] keys, byte[] command, TimeSpan timeout) =>
            SendCommandAsync(ComputeHashSlot(keys), command, timeout);

        private Task<byte[]> SendCommandAsync(uint slot, byte[] command, TimeSpan timeout) =>
            GetNodeForSlot(slot).SendCommandAsync(command, timeout);

        public Task<byte[]> SendCommandAsync(string key, byte[] command, CancellationToken cancellation) =>
            SendCommandAsync(ComputeHashSlot(key), command, cancellation);

        public Task<byte[]> SendCommandAsync(string[] keys, byte[] command, CancellationToken cancellation) =>
            SendCommandAsync(ComputeHashSlot(keys), command, cancellation);

        private Task<byte[]> SendCommandAsync(uint slot, byte[] command, CancellationToken cancellation) =>
            GetNodeForSlot(slot).SendCommandAsync(command, cancellation);

        public Task<byte[]> SendCommandAsync(string key, byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            SendCommandAsync(ComputeHashSlot(key), command, timeout, cancellationToken);

        public Task<byte[]> SendCommandAsync(string[] keys, byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            SendCommandAsync(ComputeHashSlot(keys), command, timeout, cancellationToken);

        private Task<byte[]> SendCommandAsync(uint slot, byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            GetNodeForSlot(slot).SendCommandAsync(command, timeout, cancellationToken);

        private IRedisNodeConnection GetNodeForSlot(uint slot)
        {
            var node = this.FirstOrDefault(x => !(x.Slots is null) && x.Slots.ContainsSlot(slot));

            return node;
        }


        public void Connect()
        {
            foreach (var connection in this)
            {
                connection.Connect();

                Console.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
            }
        }

        public async Task ConnectAsync()
        {
            foreach (var connection in this)
            {
                await connection.ConnectAsync();

                Trace.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
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