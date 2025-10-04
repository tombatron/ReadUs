using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;
using static ReadUs.Extras.AsyncTools;
using static ReadUs.Extras.SocketTools;

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
        _existingClusterNodes = clusterNodesResult ?? throw new Exception("Cluster nodes were null. That's weird.");

        _configuration = configuration;
    }

    /// <summary>
    /// Get an instance of the `RedisDatabase` which is a lightweight class that facilitates access to the underlying
    /// persistent connection to Redis. 
    /// </summary>
    /// <param name="databaseId">Will always be 0 since clusters do not support logical databases. Setting this parameter will have no effect.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task<IRedisDatabase> GetDatabase(int databaseId = 0,
        CancellationToken cancellationToken = default)
    {
        // TODO: Should I add a trace warning here or something if the database ID isn't 0?        
        await WaitWhileAsync(() => _isReinitializing, cancellationToken);

        var database = new RedisDatabase(this, 0);

        return database;
    }

    internal override async Task<IRedisConnection> GetConnection()
    {
        if (_backingPool.TryDequeue(out var connection))
        {
            return connection;
        }

        // TODO: Going to want to put something here so recheck the cluster configuration every now and then to make
        //       sure that the topology hasn't changed.

        var newConnection = new RedisClusterConnection(_existingClusterNodes, _configuration.ConnectionsPerNode);

        _allConnections.Add(newConnection);

        if (!newConnection.IsConnected)
        {
            await newConnection.ConnectAsync();
        }

        return newConnection;
    }

    private int _failures = 0;

    internal override void ReturnConnection(IRedisConnection connection)
    {
        if (connection.IsFaulted)
        {
            _failures++;

            if (_failures < 5)
            {
                Trace.WriteLine("!!![CONNECTION FAULED]: Disposing...");

                connection.Dispose();
            }
            else
            {
                Trace.WriteLine("!!![CONNECTION FAULTED]: REINITIALIZING THE CLUSTER POOL!!!");

                Reinitialize();

                _failures = 0;
            }
        }
        else
        {
            _backingPool.Enqueue(connection);
        }
    }

    public static Result<RedisConnectionPool> Create(ClusterNodesResult? clusterNodesResult, RedisConnectionConfiguration configuration)
    {
        // Create the pool instance. 
        var pool = new RedisClusterConnectionPool(clusterNodesResult, configuration);
        
        // Let's get a connection. In this case, we're getting a cluster connection which is expected to have a connection to 
        // each node. 
        
        // Let's check each node to make sure it's good to go by sending a ping command. 
        
        // If we don't get a response back from each node, then we're going to return an error. 
        
        // Looks like we're good to go, let's return OK. 
        return Result<RedisConnectionPool>.Ok(pool);
    }

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
        Trace.WriteLine("reinit - reinit flag set.");

        // Dispose of all connections.
        DisposeAllConnections();
        Trace.WriteLine("reinit - all connections disposed");

        // Clear out that collection. 
        _allConnections.Clear();
        Trace.WriteLine("reinit - all connections collection has been cleared");

        // All of the connections in the backing pool should be closed now,
        // we'll clear those out too.
        _backingPool.Clear();
        Trace.WriteLine("reinit - backing pool has been cleared.");

        // Now let's initialize the connection pool again. 
        
        // Instead of using the original connection string which might point to a broken node, 
        // we're going to try and reuse the cluster information that we have from the initial 
        // connection. 
        foreach (var node in _existingClusterNodes.Where(x=> x.Address is not null))
        {
            if (IsSocketAvailable(node.Address!.IpAddress, node.Address.RedisPort))
            {
                // Reusing the configuration information extracted from the connection string, lets
                // reprobe the cluster and get a new configuration. 
                if (TryGetClusterInformation(node, out var clusterNodes))
                {
                    Console.WriteLine($"reinit - cluster info acquired: {clusterNodes}");
                    // It looks like we were able to talk to the cluster to get a new configuration. Let's
                    // provide that collection to the existing `_existingClusterNodes` field...
                    _existingClusterNodes = clusterNodes;

                    // I think we're all done here, let's set the `_isReinitializing` flag back to false
                    // so we can get on with our work.
                    _isReinitializing = false;

                    Console.WriteLine("reinit - clustered pool reinitialized.");

                    return;
                }
            }
        }

        throw new Exception("Could not initialize the connection pool.");
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

    internal static bool TryGetClusterInformation(RedisConnectionConfiguration configuration,
        [NotNullWhen(true)] out ClusterNodesResult? clusterNodesResult)
    {
        // First, let's create a connection to whatever server that was provided.
        using var probingConnection = new RedisConnection(configuration);

        // Now let's... connect.
        probingConnection.Connect();

        // Next, execute the `cluster nodes` command to get an inventory of the cluster.
        var rawResult = probingConnection.SendCommand(RedisCommandEnvelope.CreateClusterNodesCommand());

        if (rawResult is Error<byte[]>)
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