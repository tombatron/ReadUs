using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReadUs
{
    public class RedisClusterConnectionPool : RedisConnectionPool
    {
        private readonly ConcurrentQueue<IRedisConnection> _backingPool = new ConcurrentQueue<IRedisConnection>();
        private readonly List<RedisClusterConnection> _allConnections = new List<RedisClusterConnection>();

        private ClusterNodesResult _existingClusterNodes;
        private int _connectionsPerNode;

        internal RedisClusterConnectionPool(ClusterNodesResult clusterNodesResult, int connectionsPerNode)
        {
            // TODO: Think about how to make this more robust. This won't survive any kind of change
            //       to the cluster. 
            _existingClusterNodes = clusterNodesResult;

            _connectionsPerNode = connectionsPerNode;
        }

        public override async Task<IRedisDatabase> GetAsync()
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

        public override void ReturnConnection(IRedisConnection connection) =>
            _backingPool.Enqueue(connection);

        public override void Dispose()
        {
            foreach (var connection in _allConnections)
            {
                connection.Dispose();
            }
        }
    }
}