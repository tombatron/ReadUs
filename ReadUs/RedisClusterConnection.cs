using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    // RedisClusterConnection is essentially just a collection of connections to the various
    // redis cluster nodes. 
    public class RedisClusterConnection : List<RedisNodeConnection>, IRedisConnection
    {
        private readonly int _connectionsPerNode;

        public RedisClusterConnection(ClusterNodesResult nodes, int connectionsPerNode = 1)
        {
            _connectionsPerNode = connectionsPerNode;

            foreach (var node in nodes)
            {
                for (var i = 0; i < connectionsPerNode; i++)
                {
                    this.Add(new RedisNodeConnection(node));
                }
            }
        }

        public bool IsConnected => this.All(x => x.IsConnected);

        public byte[] SendCommand(RedisKey key, byte[] command, TimeSpan timeout) =>
            SendCommand(key.ToArray(), command, timeout);

        public byte[] SendCommand(RedisKey[] keys, byte[] command, TimeSpan timeout) =>
            GetNodeForKeys(keys).SendCommand(keys, command, timeout);

        public byte[] SendCommand(byte[] command, TimeSpan timeout) =>
            this.First().SendCommand(command, timeout);

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

        // This is kind of chunky. If I'm not provided any keys, should I just arbitrariliy pick a connection? I'll have to think about that. 
        public Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotImplementedException("Cluster commands require keys...");

        private Random _rand = new Random();

        private IRedisNodeConnection GetNodeForKey(RedisKey key)
        {
            var qualfiedConnections = this.Where(x => !(x.Slots is null) && x.Slots.ContainsSlot(key.Slot));

            var connection =  qualfiedConnections.ElementAt(_rand.Next(_connectionsPerNode));

            Trace.WriteLine($"Using connection: {connection.ConnectionName}");

            return connection;
        }


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

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            foreach (var connection in this)
            {
                await connection.ConnectAsync(cancellationToken);

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