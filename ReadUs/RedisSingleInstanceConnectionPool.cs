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
        var connection = GetReadUsConnection();

        if (!connection.IsConnected)
        {
            await connection.ConnectAsync();
        }

        return new RedisSingleInstanceDatabase(connection, this);
    }

    public override async Task<IRedisConnection> GetConnection()
    {
        var connection = GetReadUsConnection();

        if (!connection.IsConnected)
        {
            await connection.ConnectAsync();
        }

        return connection;
    }

    private IRedisConnection GetReadUsConnection()
    {
        if (_backingPool.TryDequeue(out var connection))
        {
            return connection;
        }

        var newConnection = new RedisConnection(_configuration);

        _allConnections.Add(newConnection);

        return newConnection;
    }

    public override void ReturnConnection(IRedisConnection connection)
    {
        _backingPool.Enqueue(connection);
    }

    public override void Dispose()
    {
        foreach (var connection in _allConnections)
        {
            connection.Dispose();
        }
    }
}