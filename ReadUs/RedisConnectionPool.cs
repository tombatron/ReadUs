using System.Diagnostics;
using System.Threading.Channels;
using ReadUs.Commands;
using ReadUs.Commands.ResultModels;
using ReadUs.Exceptions;
using static ReadUs.Extras.SocketTools;

namespace ReadUs;

internal abstract record PoolRequest;
internal record GetConnectionRequest(TaskCompletionSource<IRedisConnection> Response) : PoolRequest;
internal record ReturnConnectionRequest(IRedisConnection Connection) : PoolRequest;
internal record ReinitializeRequest(TaskCompletionSource<bool> Response) : PoolRequest;

public class RedisConnectionPool : IRedisConnectionPool
{
    private readonly Channel<PoolRequest> _requestChannel;
    private readonly CancellationTokenSource _shutdownCts;
    private readonly Task _processorTask;
    
    private readonly List<IRedisConnection> _allConnections = new();
    private readonly Queue<IRedisConnection> _availableConnections = new();
    private int _failures = 0;
    
    private readonly RedisConnectionConfiguration[] _configurations;
    private readonly Func<RedisConnectionConfiguration[], IRedisConnection> _connectionFactory;

    private RedisConnectionPool(RedisConnectionConfiguration[] configurations, Func<RedisConnectionConfiguration[], IRedisConnection> connectionFactory)
    {
        _configurations = configurations;
        _connectionFactory = connectionFactory;
        
        _requestChannel = Channel.CreateUnbounded<PoolRequest>(new UnboundedChannelOptions{SingleReader = true, SingleWriter = false});
        _shutdownCts = new();
        _processorTask = Task.Run(() => ProcessRequestsAsync(_shutdownCts.Token));
    }

    public IRedisDatabase GetDatabase(int databaseId = 0) => new RedisDatabase(this, databaseId);
    
    public void Dispose()
    {
        // This should shutdown our processing task gracefully.
        _requestChannel.Writer.Complete();
        
        // Wait for the processing task to finish.
        _shutdownCts.Cancel();

        try
        {
            // Wait a second for the task to complete.
            _processorTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
            // This is expected, we're just going to swallow it. 
        }
        
        DisposeAllConnections();
        
        _shutdownCts.Dispose();
    }

    internal async Task<IRedisConnection> GetConnection()
    {
        var tcs = new TaskCompletionSource<IRedisConnection>();
        var request = new GetConnectionRequest(tcs);
        
        await _requestChannel.Writer.WriteAsync(request);
        
        return await tcs.Task;
    }

    internal void ReturnConnection(IRedisConnection connection)
    {
        var request = new ReturnConnectionRequest(connection);
        
        // Fire and forget, we don't need to await this.
        _requestChannel.Writer.TryWrite(request);
    }
    
    private async Task ProcessRequestsAsync(CancellationToken cancellationToken)
    {
        await foreach(var request in _requestChannel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                switch (request)
                {
                    case GetConnectionRequest getConnectionRequest:
                        await HandleGetConnection(getConnectionRequest);
                        break;
                    
                    case ReturnConnectionRequest returnConnectionRequest:
                        HandleReturnConnection(returnConnectionRequest);
                        break;
                    
                    case ReinitializeRequest reinitializeRequest:
                        HandleReinitialize(reinitializeRequest);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Obviously we're going to do something a little more fancy here...
                Console.WriteLine($"Error processing request: {ex}");
            }
        }
    }    
    
    private void Reinitialize()
    {
        var request = new ReinitializeRequest(new TaskCompletionSource<bool>());
        
        _requestChannel.Writer.TryWrite(request);

        _failures = 0;
    }
    
    private async Task HandleGetConnection(GetConnectionRequest request)
    {
        try
        {
            IRedisConnection connection;

            if (_availableConnections.TryDequeue(out var conn))
            {
                connection = conn;
            }
            else
            {
                // Create a new connection using the existing configuration object.
                var newConnection = _connectionFactory(_configurations);

                // Add a reference to the new connection to the existing collection of connections. Heh.
                _allConnections.Add(newConnection);

                if (!newConnection.IsConnected)
                {
                    await newConnection.ConnectAsync();
                }

                connection = newConnection;
            }

            request.Response.SetResult(connection);
        }
        catch (Exception ex)
        {
            request.Response.SetException(ex);
        }
    }

    private void HandleReturnConnection(ReturnConnectionRequest request)
    {
        if (request.Connection.IsFaulted)
        {
            _failures++;

            if (_failures < 5)
            {
                Trace.WriteLine("!!![CONNECTION FAULTED]: Disposing...");
                request.Connection.Dispose();
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
            _availableConnections.Enqueue(request.Connection);
        }
    }
    
    private void HandleReinitialize(ReinitializeRequest request)
    {
        try
        {
            Trace.WriteLine("Reinitializing connection pool...");
            
            DisposeAllConnections();

            _allConnections.Clear();
            _availableConnections.Clear();
            
            Trace.WriteLine("Reinitialization complete.");

            request.Response.SetResult(true);
        }
        catch (Exception ex)
        {
            request.Response.SetException(ex);
        }
    }

    private void DisposeAllConnections()
    {
        foreach (var connection in _allConnections)
        {
            connection.Dispose();
        }
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
        
        using var probingConnection = new RedisConnection(configuration);
        
        probingConnection.Connect();

        var nodesResult = probingConnection.Nodes().GetAwaiter().GetResult();

        if (nodesResult is Error<ClusterNodesResult>)
        {
            return false;
        }
        
        var nodes = nodesResult.Unwrap();

        if (nodes.HasError)
        {
            return false;
        }

        nodeConfigurations = nodes.Select(x=> 
            new RedisConnectionConfiguration(x.Address!.IpAddress.ToString(), x.Address.RedisPort, configuration.ConnectionName)).ToArray();

        return true;
    }    
}