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
        var nodeResult = GetNodeForKeys(command);

        if (nodeResult is Error<IRedisConnection> nodeError)
        {
            return Result<byte[]>.Error(nodeError.Message);
        }

        if (nodeResult is Ok<IRedisConnection> nodeOk)
        {
            var redisNode = nodeOk.Value;
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

        return Result<byte[]>.Error(
            "I need to think how to avoid needing to put this here because of a required return.");
    }

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        var nodeResult = GetNodeForKeys(command);

        if (nodeResult is Error<IRedisConnection> nodeError)
        {
            return Result<byte[]>.Error(nodeError.Message);
        }

        if (nodeResult is Ok<IRedisConnection> nodeOk)
        {
            var redisNode = nodeOk.Value;

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

        return Result<byte[]>.Error(
            "I need to think how to avoid needing to put this here because of a required return.");
    }

    private bool IsResponseFaulted(Result<byte[]> response)
    {
        // TODO: I'm going to make this more robust, but right now I'm just trying to see if I can get it to work. 
        if (response is Error<byte[]> error && (error.Message.StartsWith("[TIMEOUT]") || error.Message.StartsWith("[SOCKET_EXCEPTION]")))
        {
            return true;
        }

        return false;
    }

    public Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse,
        CancellationToken cancellationToken = default)
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
        var slots = new HashSet<int>();

        foreach (var connection in this)
        {
            var slotsResult = connection.Slots();
            var ownedSlots = slotsResult.UnwrapOr(ClusterSlots.Default).OwnedSlots;
            
            slots.UnionWith(ownedSlots);
        }

        return slots.Count == 16_384;
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

    private Result<IRedisConnection> GetNodeForKey(RedisKey key)
    {
        // We're not using a linq statement here because we're now executing commands against a (supposedly) open
        // connection, we need to be super deliberate about this. 
        foreach (var connection in this)
        {
            var roleResult = connection.Role();

            if (roleResult is Error<RoleResult> roleError)
            {
                return Result<IRedisConnection>.Error(roleError.Message);
            }

            // TODO: Fix Tombatron.Results
            // We should be able to do this: if (roleResult is Ok<RoleResult> { Value: PrimaryRoleResult })
            //                      or this: if (roleResult is Ok<RoleResult> roleOk && roleOk.Value is PrimaryRoleResult)
            // i guess....
            if (roleResult is Ok<RoleResult> roleOk)
            {
                if (roleOk.Value is PrimaryRoleResult)
                {
                    var slotsResult = connection.Slots();

                    if (slotsResult is Error<ClusterSlots> slotsError)
                    {
                        return Result<IRedisConnection>.Error(slotsError.Message);
                    }
                    
                    // TODO: Fix Tombatron.Results
                    // We should be able to do this: if (slotsResult is Ok<ClusterSlots> slotsOk && slotsOk.Value.ContainsSlot(key.Slot)) 
                    if (slotsResult is Ok<ClusterSlots> slotsOk)
                    {
                        if (slotsOk.Value.ContainsSlot(key.Slot))
                        {
                            return Result<IRedisConnection>.Ok(connection);
                        }
                    }
                }
                
            }
        }

        return Result<IRedisConnection>.Error("We couldn't find a connection for your command.");
    }

    private Result<IRedisConnection> GetNodeForKeys(RedisCommandEnvelope command)
    {
        // If the command being executed doesn't have any keys, then we don't really 
        // have anything to decide which node to execute the command against. So for now,
        // we'll just return the first connection in the current collection.
        if (command.Keys is null)
        {
            return Result<IRedisConnection>.Ok(this[0]);
        }

        // Check if the keys all belong to the same slot. 
        if (!command.AllKeysInSingleSlot)
        {
            return Result<IRedisConnection>.Error("Multi-key operations against different slots isn't supported yet.");
        }

        Trace.WriteLine($"Getting connection for `{command.Command}` for key `{command.Keys.First()}`.");

        // Everything is in the same slot so just go get a node. 
        return GetNodeForKey(command.Keys.First());
    }
}