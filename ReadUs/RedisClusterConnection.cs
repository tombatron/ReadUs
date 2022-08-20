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
    public class RedisClusterConnection : List<RedisNodeConnection>, IRedisConnection
    {
        public RedisClusterConnection(ClusterNodesResult nodes)
        {
            foreach (var node in nodes)
            {
                this.Add(new RedisNodeConnection(node));
            }
        }

        public bool IsConnected => this.All(x => x.IsConnected);

        public byte[] SendCommand(RedisKey key, byte[] command, TimeSpan timeout) =>
            SendCommand(key.ToArray(), command, timeout);

        public byte[] SendCommand(RedisKey[] keys, byte[] command, TimeSpan timeout) =>
            GetNodeForKeys(keys).SendCommand(keys, command, timeout);

        public Task<byte[]> SendCommandAsync(RedisKey key, byte[] command) =>
            SendCommandAsync(key.ToArray(), command);

        public Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command) =>
            GetNodeForKeys(keys).SendCommandAsync(keys, command);

        public Task<byte[]> SendCommandAsync(RedisKey key, byte[] command, TimeSpan timeout) =>
            SendCommandAsync(key.ToArray(), command, timeout);

        public Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command, TimeSpan timeout) =>
            GetNodeForKeys(keys).SendCommandAsync(keys, command, timeout);

        public Task<byte[]> SendCommandAsync(RedisKey key, byte[] command, CancellationToken cancellationToken) =>
            SendCommandAsync(key.ToArray(), command, cancellationToken);

        public Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command, CancellationToken cancellationToken) =>
            GetNodeForKeys(keys).SendCommandAsync(keys, command, cancellationToken);

        public Task<byte[]> SendCommandAsync(RedisKey key, byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            SendCommandAsync(key.ToArray(), command, timeout, cancellationToken);

        public Task<byte[]> SendCommandAsync(RedisKey[] keys, byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            GetNodeForKeys(keys).SendCommandAsync(keys, command, timeout, cancellationToken);

        private IRedisNodeConnection GetNodeForKey(RedisKey key) =>
            this.FirstOrDefault(x => !(x.Slots is null) && x.Slots.ContainsSlot(key.Slot));

        private IRedisNodeConnection GetNodeForKeys(RedisKey[] keys)
        {
            // Check if the keys all belong to the same slot. 
            if (!keys.All(x => x.Slot == keys[0].Slot))
            {
                throw new Exception("Multi-key operations against different slots isn't supported yet.");
            }

            // Everything is in the same slot so just go get a node. 
            return GetNodeForKey(keys[0]);
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