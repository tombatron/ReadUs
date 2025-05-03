using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReadUs;

public class RedisSingleInstanceConnectionPool : RedisConnectionPool
{
    private readonly List<IRedisConnection> _allConnections = new();
    private readonly ConcurrentQueue<IRedisConnection> _backingPool = new();
    private readonly RedisConnectionConfiguration _configuration;

    internal RedisSingleInstanceConnectionPool(RedisConnectionConfiguration configuration)
    {
        _configuration = configuration;
    }

    public override Task<IRedisDatabase> GetAsync() => 
        Task.FromResult(new RedisSingleInstanceDatabase(this) as IRedisDatabase);

    internal override async Task<IRedisConnection> GetConnection() // TODO: Not sure that this needs to be async...
    {
        IRedisConnection connection;
        
        if (_backingPool.TryDequeue(out var conn))
        {
            connection = conn;
        }
        else
        {
            // Create a new connection using the existing configuration object.
            var newConnection = new RedisConnection(_configuration);
            
            // Add a reference to the new connection to the existing collection
            // of existing connections.
            _allConnections.Add(newConnection);

            if (!newConnection.IsConnected)
            {
                await newConnection.ConnectAsync();
            }

            connection = newConnection;
        }

        return connection;
    }

    internal override void ReturnConnection(IRedisConnection connection) => _backingPool.Enqueue(connection);

    public override void Dispose()
    {
        foreach (var connection in _allConnections)
        {
            connection.Dispose();
        }
    }
}