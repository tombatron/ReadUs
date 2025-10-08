using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Exceptions;
using ReadUs.ResultModels;
using static ReadUs.Extras.AsyncTools;
using static ReadUs.Extras.SocketTools;

namespace ReadUs;

public class RedisConnectionPool(
    RedisConnectionConfiguration[] configurations,
    Func<RedisConnectionConfiguration[], IRedisConnection> connectionFactory) : IRedisConnectionPool
{
    private static bool _isReinitializing = false;
    private readonly List<IRedisConnection> _allConnections = new();
    private readonly ConcurrentQueue<IRedisConnection> _backingPool = new();

    private RedisConnectionPool(RedisConnectionConfiguration redisConfiguration,
        Func<RedisConnectionConfiguration[], IRedisConnection> connectionFactory) : this([redisConfiguration],
        connectionFactory)
    {
    }

    public async Task<IRedisDatabase> GetDatabase(int databaseId = 0,
        CancellationToken cancellationToken = default)
    {
        await WaitWhileAsync(() => _isReinitializing, cancellationToken);

        return new RedisDatabase(this, databaseId);
    }

    internal async Task<IRedisConnection> GetConnection()
    {
        IRedisConnection connection;

        if (_backingPool.TryDequeue(out var conn))
        {
            connection = conn;
        }
        else
        {
            // Create a new connection using the existing configuration object.
            var newConnection = connectionFactory(configurations);

            // Add a reference to the new connection to the existing collection of connections. Heh.
            _allConnections.Add(newConnection);

            if (!newConnection.IsConnected)
            {
                await newConnection.ConnectAsync();
            }

            connection = newConnection;
        }

        return connection;
    }

    private int _failures = 0;

    internal void ReturnConnection(IRedisConnection connection)
    {
        if (connection.IsFaulted)
        {
            _failures++;

            if (_failures < 5)
            {
                Trace.WriteLine("!!![CONNECTION FAULTED]: Disposing...");
                connection.Dispose();
            }
            else
            {
                Trace.WriteLine("!!![CONNECTION FAULTED]: REINITIALIZING THE CONNECTION POOL...");

                Reinitialize();

                _failures = 0;
            }
        }
        else
        {
            _backingPool.Enqueue(connection);
        }
    }

    public virtual void Dispose()
    {
        foreach (var connection in _allConnections)
        {
            connection.Dispose();
        }
    }

    private void Reinitialize()
    {
        _isReinitializing = true;
        Trace.WriteLine("reinit - reinit flag set.");

        // Dispose of all connections.
        Dispose();
        Trace.WriteLine("reinit - all connections disposed");

        // Clear out that collection. 
        _allConnections.Clear();
        Trace.WriteLine("reinit - all connections collection has been cleared");

        // All the connections in the backing pool should be closed now,
        // we'll clear those out too.
        _backingPool.Clear();
        Trace.WriteLine("reinit - backing pool has been cleared.");
    }

    public static IRedisConnectionPool Create(Uri connectionString)
    {
        RedisConnectionConfiguration[] configuration = [connectionString];

        if (!IsSocketAvailable(configuration[0].ServerAddress, configuration[0].ServerPort))
        {
            throw new RedisConnectionException($"Could not connect to this redis server: {connectionString}");
        }
        
        Func<RedisConnectionConfiguration[], IRedisConnection> connectionFactory;

        if (IsCluster(configuration.First(), out var configurations))
        {
            configuration = configurations!;
            connectionFactory = ClusterConnectionFactory;
        }
        else
        {
            connectionFactory = SingleInstanceConnectionFactory;
        }
        
        return new RedisConnectionPool(configuration, connectionFactory);
    }

    private static IRedisConnection ClusterConnectionFactory(RedisConnectionConfiguration[] configurations) =>
        new RedisClusterConnection(configurations);

    private static IRedisConnection SingleInstanceConnectionFactory(RedisConnectionConfiguration[] configurations) =>
        new RedisConnection(configurations.First());

    // TODO: Change this such that it behaves more like the helper method SocketTools.IsSocketAvailable
    internal static bool IsCluster(RedisConnectionConfiguration configuration, out RedisConnectionConfiguration[]? nodeConfigurations)
    {
        nodeConfigurations = null;
        
        // First, let's create a connection to whatever server that was provided.
        using var probingConnection = new RedisConnection(configuration);

        // Now let's... connect.
        probingConnection.Connect();

        // Next, execute the `cluster nodes` command to get an inventory of the cluster.
        var rawResult = probingConnection.SendCommand(RedisCommandEnvelope.CreateClusterNodesCommand());

        if (rawResult is Error<byte[]>)
        {
            return false;
        }

        // Handle the result of the `cluster nodes` command by populating a data structure with the 
        // addresses, role, and slots assigned to each node. 
        var nodes = new ClusterNodesResult(rawResult.Unwrap());

        if (nodes.HasError)
        {
            return false;
        }

        nodeConfigurations = nodes.Select(x=> (RedisConnectionConfiguration)x).ToArray();

        return true;
    }
}