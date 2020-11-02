using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReadUs
{
    public sealed class RedisCommandsPool : IDisposable
    {
        private readonly string _serverAddress;
        private readonly int _serverPort;

        private readonly ConcurrentQueue<IReadUsConnection> _backingPool = new ConcurrentQueue<IReadUsConnection>();
        private readonly List<IReadUsConnection> _allConnections = new List<IReadUsConnection>();

        public RedisCommandsPool(string serverAddress, int serverPort)
        {
            _serverAddress = serverAddress;
            _serverPort = serverPort;
        }

        public async Task<IRedisCommands> GetAsync()
        {
            var connection = GetReadUsConnection();

            await connection.ConnectAsync();

            return new RedisCommands(connection, this);
        }

        private IReadUsConnection GetReadUsConnection()
        {
            if (_backingPool.TryDequeue(out var connection))
            {
                return connection;
            }
            else
            {
                var newConnection = new ReadUsConnection(_serverAddress, _serverPort);

                _allConnections.Add(newConnection);

                return newConnection;
            }
        }

        internal void ReturnConnection(IReadUsConnection connection)
        {
            _backingPool.Enqueue(connection);
        }

        public void Dispose()
        {
            foreach (var connection in _allConnections)
            {
                connection.Dispose();
            }
        }
    }
}