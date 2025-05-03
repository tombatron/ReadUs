using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;
using static ReadUs.Extras.AsyncTools;

namespace ReadUs;

public class RedisClusterConnectionPool : RedisConnectionPool
{
    private static bool _isReinitializing;
    private readonly List<RedisClusterConnection> _allConnections = new();
    private readonly ConcurrentQueue<IRedisConnection> _backingPool = new();
    private readonly RedisConnectionConfiguration _configuration;

    private ClusterNodesResult _existingClusterNodes;

    internal RedisClusterConnectionPool(ClusterNodesResult? clusterNodesResult,
        RedisConnectionConfiguration configuration)
    {
        // TODO: Think about how to make this more robust. This won't survive any kind of change
        //       to the cluster. 
        _existingClusterNodes = clusterNodesResult ?? throw new Exception("Cluster nodes were null. That's weird.");

        _configuration = configuration;
    }

    // TODO: CancellationTokens...
    public override async Task<IRedisDatabase> GetAsync()
    {
        // Need to check if we are reinitializing. That shouldn't happen too often, but
        // if it does we'll want to wait until that is complete before returning anything
        // to the caller. 
        await WaitWhileAsync(() => _isReinitializing, CancellationToken.None);

        var connection = await GetConnection();

        if (!connection.IsConnected)
        {
            await connection.ConnectAsync();
        }

        var database = new RedisClusterDatabase(this);

        database.RedisServerExceptionEvent += OnRedisServerException;

        return database;
    }

    internal override Task<IRedisConnection> GetConnection()
    {
        if (_backingPool.TryDequeue(out var connection))
        {
            return Task.FromResult(connection);
        }
        
        var newConnection = new RedisClusterConnection(_existingClusterNodes, _configuration.ConnectionsPerNode);

        _allConnections.Add(newConnection);

        return Task.FromResult(newConnection as IRedisConnection);
    }

    internal override void ReturnConnection(IRedisConnection connection) => _backingPool.Enqueue(connection);

    public override void Dispose() => DisposeAllConnections();

    private void DisposeAllConnections()
    {
        foreach (var connection in _allConnections)
        {
            connection.Dispose();
        }
    }

    private void Reinitialize()
    {
        _isReinitializing = true;
        Console.WriteLine("reinit - reinit flag set.");

        // Dispose of all connections.
        DisposeAllConnections();
        Console.WriteLine("reinit - all connections disposed");

        // Clear out that collection. 
        _allConnections.Clear();
        Console.WriteLine("reinit - all connections collection has been cleared");

        // All of the connections in the backing pool should be closed now,
        // we'll clear those out too.
        _backingPool.Clear();
        Console.WriteLine("reinit - backing pool has been cleared.");

        // Now let's initialize the connection pool again. 

        // Reusing the configuration information extracted from the connection string, lets
        // reprobe the cluster and get a new configuration. 
        if (TryGetClusterInformation(_configuration, out var clusterNodes))
        {
            Console.WriteLine($"reinit - cluster info acquired: {clusterNodes}");
            // It looks like we were able to talk to the cluster to get a new configuration. Let's
            // provide that collection to the existing `_existingClusterNodes` field...
            _existingClusterNodes = clusterNodes;

            // I think we're all done here, let's set the `_isReinitializing` flag back to false
            // so we can get on with our work.
            _isReinitializing = false;

            Console.WriteLine("reinit - clustered pool reinitialized.");
        }
        else
        {
            throw new Exception("Could not initialize the connection pool.");
        }
    }

    private void OnRedisServerException(object sender, RedisServerExceptionEventArgs args)
    {
        Console.WriteLine($"`OnRedisServerException` event raised with error: '{args.Exception.Message}'");

        var exception = args.Exception;

        // If the `RedisError` is null, then there's nothing for us to evaluate now is there?
        if (exception.RedisError is null)
        {
            return;
        }

        var redisErrorMessage = exception.RedisError;

        // If the error string exists and starts with `MOVED` then we tried to operate on a key
        // that exists on a different node than we send the command to. This could indicate
        // that the node we're connected to has "shifted" roles or that the key slots have been
        // redistributed, let's go ahead and re-initialize the pool.
        if (redisErrorMessage.StartsWith("MOVED"))
        {
            Console.WriteLine("Detected an error reinitializing.");
            
            Reinitialize();
        }
    }

    internal static bool TryGetClusterInformation(RedisConnectionConfiguration configuration, [NotNullWhen(true)] out ClusterNodesResult? clusterNodesResult)
    {
        // First, let's create a connection to whatever server that was provided.
        using var probingConnection = new RedisConnection(configuration);

        // Now let's... connect.
        probingConnection.Connect();

        // Next, execute the `cluster nodes` command to get an inventory of the cluster.
        var rawResult = probingConnection.SendCommand(RedisCommandEnvelope.CreateClusterNodesCommand());
        
        if(rawResult is Error<byte[]>)
        {
            clusterNodesResult = null;

            return false;
        }

        // Handle the result of the `cluster nodes` command by populating a data structure with the 
        // addresses, role, and slots assigned to each node. 
        var nodes = new ClusterNodesResult(rawResult.Unwrap());

        if (nodes.HasError)
        {
            clusterNodesResult = null;

            return false;
        }

        clusterNodesResult = nodes;

        return true;
    }
}