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
            for (var i = 0; i < connectionsPerNode; i++)
                Add(new RedisNodeConnection(node));
    }

    public bool IsConnected => this.All(x => x.IsConnected);

    public byte[] SendCommand(RedisCommandEnvelope command)
    {
        return GetNodeForKeys(command).SendCommand(command);
    }

    public Task<byte[]> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        return GetNodeForKeys(command).SendCommandAsync(command);
    }

    public void Connect()
    {
        foreach (var connection in this)
        {
            connection.Connect();

            Console.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
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
        foreach (var connection in this) connection.Dispose();
    }

    // TODO: Let's find a way to get rid of these... This will require a redesign of some sort. 

    public RoleResult Role()
    {
        throw new NotImplementedException("This command doesn't really make sense here...");
    }

    public Task<RoleResult> RoleAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("This command doesn't really make sense here...");
    }

    // This is kind of chunky. If I'm not provided any keys, should I just arbitrariliy pick a connection? I'll have to think about that. 
    public Task<byte[]> SendCommandAsync(byte[] command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        throw new NotImplementedException("Cluster commands require keys...");
    }

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
        if (command.Keys is null) return this[0];

        // Check if the keys all belong to the same slot. 
        if (!command.AllKeysInSingleSlot)
            throw new Exception("Multi-key operations against different slots isn't supported yet.");

        // Everything is in the same slot so just go get a node. 
        return GetNodeForKey(command.Keys.First());
    }
}