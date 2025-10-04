using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

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

    public override Task<IRedisDatabase>
        GetDatabase(int databaseId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult<IRedisDatabase>(new RedisDatabase(this, databaseId));

    internal override async Task<IRedisConnection> GetConnection()
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

    public static Result<IRedisConnectionPool> Create(RedisConnectionConfiguration configuration)
    {
        // Create the pool instance. 
        var pool = new RedisSingleInstanceConnectionPool(configuration);

        // Let's get a connection. In this case we're getting a singular connection to a single Redis backend. 
        var connection = pool.GetConnection().GetAwaiter().GetResult();

        // Let's check the connection to make sure it's good by sending a ping command.
        var pingResult = connection.Ping();

        if (pingResult is Error<PingResult> err)
        {   
            return Result<IRedisConnectionPool>.Error("There was an error creating the connection pool.", err);
        }

        // TODO: Maybe it's OK to only handle Error cases? Food for though I guess. 
        _ = pingResult.Unwrap();

        return Result<IRedisConnectionPool>.Ok(pool);
    }

    public override void Dispose()
    {
        foreach (var connection in _allConnections)
        {
            connection.Dispose();
        }
    }
}