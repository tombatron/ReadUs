using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReadUs
{
    public class RedisCommandsPool : IDisposable
    {
        protected readonly string _serverAddress;
        protected readonly int _serverPort;

        private readonly ConcurrentQueue<IRedisConnection> _backingPool = new ConcurrentQueue<IRedisConnection>();
        private readonly List<IRedisConnection> _allConnections = new List<IRedisConnection>();

        public RedisCommandsPool(string serverAddress, int serverPort)
        {
            _serverAddress = serverAddress;
            _serverPort = serverPort;
        }

        public async Task<IRedisDatabase> GetAsync()
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

        internal void ReturnConnection(IRedisConnection connection) =>
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