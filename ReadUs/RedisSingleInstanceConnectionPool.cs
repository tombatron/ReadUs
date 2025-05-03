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

    public override async Task<IRedisDatabase> GetAsync()
    {
        var connection = await GetConnection();

        if (!connection.IsConnected)
        {
            await connection.ConnectAsync();
        }

        return new RedisSingleInstanceDatabase(this);
    }

    internal override Task<IRedisConnection> GetConnection() // TODO: Not sure that this needs to be async...
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

            connection = newConnection;
        }

        return Task.FromResult(connection);
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