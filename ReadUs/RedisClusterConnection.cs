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
public class RedisClusterConnection : List<RedisConnection>, IRedisConnection
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
                Add(new RedisConnection(node));
            }            
        }
    }

    // TODO: This is a bit of a hack. We need to figure out a better way to handle this.
    public string ConnectionName => "Redis Cluster Connection";
    public bool IsConnected => this.All(x => x.IsConnected);
    public bool IsFaulted { get; private set; }

    public Result<byte[]> SendCommand(RedisCommandEnvelope command)
    {
        var redisNode = GetNodeForKeys(command);
        
        var response = redisNode.SendCommand(command);

        if (response is Error<byte[]> err)
        {
            IsFaulted = IsResponseFaulted(response);
        }

        if (response is Ok<byte[]> ok)
        {
            // TODO: Get rid of this after updating Tombatron.Results.
        }

        return response;
    }

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default) 
    {
        var redisNode = GetNodeForKeys(command);
        
        var response = await redisNode.SendCommandAsync(command, cancellationToken);

        if (response is Error<byte[]> err)
        {
            IsFaulted = IsResponseFaulted(response);
        }

        if (response is Ok<byte[]> ok)
        {
            // TODO: Get rid of this after updating Tombatron.Results.
        }

        return response;
    }

    private bool IsResponseFaulted(Result<byte[]> response)
    {
        // TODO: I'm going to make this more robust, but right now I'm just trying to see if I can get it to work. 
        if (response is Error<byte[]> error && error.Message.StartsWith("[TIMEOUT]"))
        {
            return true;
        }

        return false;
    }

    public Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Result Connect()
    {
        foreach (var connection in this)
        {
            connection.Connect();

            Trace.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
        }

        return Result.Ok;
    }

    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var connection in this)
        {
            await connection.ConnectAsync(cancellationToken);

            Trace.WriteLine($"{connection.EndPoint.Address}:{connection.EndPoint.Port} connected.");
        }

        if (CheckHealth())
        {
            return Result.Ok;
        }

        return Result.Error("Connection failed health check.");
    }

    private bool CheckHealth()
    {
        var slotRanges = new List<ClusterSlots.SlotRange[]>();
        
        foreach (var connection in this)
        {
            var slots = connection.Slots();
            slotRanges.Add(slots.Unwrap().SlotRanges);
        }

        return true;
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
    
    public Result<ClusterSlots> Slots() => Result<ClusterSlots>.Error("NO-OP for now...");
    
    public Task<Result<ClusterSlots>> SlotsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ClusterSlots>.Error("NO-OP for now..."));
    
    private IRedisConnection GetNodeForKey(RedisKey key)
    {
        var qualifiedConnections = this.Where(x => x.Slots().Unwrap().ContainsSlot(key.Slot) && x.Role().Unwrap() is PrimaryRoleResult);

        var connection = qualifiedConnections.ElementAt(_rand.Next(_connectionsPerNode));

        Trace.WriteLine($"Using connection: {connection.ConnectionName}");

        return connection;
    }

    private IRedisConnection GetNodeForKeys(RedisCommandEnvelope command)
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
        
        Trace.WriteLine($"Getting connection for `{command.Command}` for key `{command.Keys.First()}`.");

        // Everything is in the same slot so just go get a node. 
        return GetNodeForKey(command.Keys.First());
    }
}