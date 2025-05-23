using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs;

// RedisClusterConnection is essentially just a collection of connections to the various
// redis cluster nodes. 
public class RedisClusterConnection : List<RedisNodeConnection>, IRedisConnection
{
    private readonly int _connectionsPerNode;

    private readonly Random _rand = new();

    public RedisClusterConnection(ClusterNodesResult nodes, int connectionsPerNode = 1)
    {
        _connectionsPerNode = connectionsPerNode;

        foreach (var node in nodes)
        {
            for (var i = 0; i < connectionsPerNode; i++)
            {
                Add(new RedisNodeConnection(node));
            }            
        }
    }

    // TODO: This is a bit of a hack. We need to figure out a better way to handle this.
    public string ConnectionName => "Redis Cluster Connection";
    public bool IsConnected => this.All(x => x.IsConnected);

    public Result<byte[]> SendCommand(RedisCommandEnvelope command) => GetNodeForKeys(command).SendCommand(command);

    public Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default) => 
        GetNodeForKeys(command).SendCommandAsync(command, cancellationToken);

    public Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public void Connect()
    {
        foreach (var connection in this)
        {
            connection.Connect();

            Trace.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var connection in this)
        {
            await connection.ConnectAsync(cancellationToken);

            Trace.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
        }
    }

    public void Dispose()
    {
        foreach (var connection in this)
        {
            connection.Dispose();
        }
    }

    // TODO: Let's find a way to get rid of these... This will require a redesign of some sort. 

    public Result<RoleResult> Role() => Result<RoleResult>.Error("This command doesn't really make sense here...");

    public Task<Result<RoleResult>> RoleAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<RoleResult>.Error("This command doesn't really make sense here..."));
    
    private IRedisNodeConnection GetNodeForKey(RedisKey key)
    {
        var qualifiedConnections = this.Where(x => !(x.Slots is null) && x.Slots.ContainsSlot(key.Slot));

        var connection = qualifiedConnections.ElementAt(_rand.Next(_connectionsPerNode));

        Trace.WriteLine($"Using connection: {connection.ConnectionName}");

        return connection;
    }

    private IRedisNodeConnection GetNodeForKeys(RedisCommandEnvelope command)
    {
        // If the command being executed doesn't have any keys, then we don't really 
        // have anything to decide which node to execute the command against. So for now,
        // we'll just return the first connection in the current collection.
        if (command.Keys is null)
        {
            return this[0];
        }

        // Check if the keys all belong to the same slot. 
        if (!command.AllKeysInSingleSlot)
        {
            throw new Exception("Multi-key operations against different slots isn't supported yet.");
        }

        // Everything is in the same slot so just go get a node. 
        return GetNodeForKey(command.Keys.First());
    }
}