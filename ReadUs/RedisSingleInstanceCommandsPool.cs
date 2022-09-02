using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReadUs
{
    public class RedisSingleInstanceCommandsPool : RedisCommandsPool
    {
        private readonly ConcurrentQueue<IRedisConnection> _backingPool = new ConcurrentQueue<IRedisConnection>();
        private readonly List<IRedisConnection> _allConnections = new List<IRedisConnection>();

        public RedisSingleInstanceCommandsPool(string serverAddress, int serverPort)
        { }

        public override async Task<IRedisDatabase> GetAsync()
        {
            var connection = GetReadUsConnection();

            if (!connection.IsConnected)
            {
                await connection.ConnectAsync();
            }

            return new RedisSingleInstanceDatabase(connection, this);
        }

        private IRedisConnection GetReadUsConnection()
        {
            if (_backingPool.TryDequeue(out var connection))
            {
                return connection;
            }

            var newConnection = new RedisConnection(_serverAddress, _serverPort);

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