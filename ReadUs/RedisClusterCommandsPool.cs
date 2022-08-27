using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public class RedisClusterCommandsPool : IRedisConnectionPool
    {
        private readonly ConcurrentQueue<IRedisConnection> _backingPool = new ConcurrentQueue<IRedisConnection>();
        private readonly List<RedisClusterConnection> _allConnections = new List<RedisClusterConnection>();

        private ClusterNodesResult _existingClusterNodes;
        private int _connectionsPerNode;

        public RedisClusterCommandsPool(string serverAddress, int serverPort, int connectionsPerNode)
        {
            // First, let's create a connection to whatever server that was provided.
            using var initialConnection = new RedisConnection(serverAddress, serverPort);
            initialConnection.Connect();

            // Next, execute the `cluster nodes` command to get an inventory of the cluster.
            var rawCommand = Encode(Cluster, ClusterSubcommands.Nodes);
            var rawResult = initialConnection.SendCommand(rawCommand, TimeSpan.FromMilliseconds(1));


            // Handle the result of the `cluster nodes` command by populating a data structure with the 
            // addresses, role, and slots assigned to each node. 
            var nodes = new ClusterNodesResult(rawResult);

            // TODO: Think about how to make this more robust. This won't survive any kind of change
            //       to the cluster. 
            _existingClusterNodes = nodes;

            _connectionsPerNode = connectionsPerNode;
        }

        public async Task<IRedisDatabase> GetAsync()
        {
            var connection = GetReadUsConnection();

            if (!connection.IsConnected)
            {
                await connection.ConnectAsync();
            }

            return new RedisClusterDatabase(connection, this);
        }

        private IRedisConnection GetReadUsConnection()
        {
            if (_backingPool.TryDequeue(out var connection))
            {
                return connection;
            }

            var newConnection = new RedisClusterConnection(_existingClusterNodes, _connectionsPerNode);

            _allConnections.Add(newConnection);

            return newConnection;
        }

        public void ReturnConnection(IRedisConnection connection) =>
            _backingPool.Enqueue(connection);

        public void Dispose()
        {
            foreach (var connection in _allConnections)
            {
                connection.Dispose();
            }
        }
    }
}