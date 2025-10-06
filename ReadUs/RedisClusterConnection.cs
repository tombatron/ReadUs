using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;
using static ReadUs.Extras.SocketTools;

namespace ReadUs;

// RedisClusterConnection is essentially just a collection of connections to the various
// redis cluster nodes. 
public class RedisClusterConnection : List<RedisConnection>, IRedisConnection
{
    private readonly Random _rand = new();

    public RedisClusterConnection(RedisConnectionConfiguration[] configurations)
    {
        var connectionsPerNode = configurations.First().ConnectionsPerNode;

        foreach (var configuration in configurations)
        {
            for (var i = 0; i < connectionsPerNode; i++)
            {
                if (IsSocketAvailable(configuration.ServerAddress, configuration.ServerPort))
                {
                    Add(new RedisConnection(configuration));
                }
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

        var redisNode = nodeResult.Unwrap();
        var response = redisNode.SendCommand(command);

        if (response is Error<byte[]>)
        {
            IsFaulted = true;
        }

        return Result<byte[]>.Ok(response.Unwrap());
    }

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        var nodeResult = GetNodeForKeys(command);

        if (nodeResult is Error<IRedisConnection> nodeError)
        {
            IsFaulted = true;
            return Result<byte[]>.Error("Error getting the correct connection for the request.", nodeError);
        }

        var connection = nodeResult.Unwrap();

        var response = await connection.SendCommandAsync(command, cancellationToken);

        IsFaulted = IsResponseFaulted(response);

        return response;
    }

    private bool IsResponseFaulted(Result<byte[]> response)
    {
        // TODO: I'm going to make this more robust, but right now I'm just trying to see if I can get it to work. 
        if (response is Error<byte[]> error &&
            (error.Message.StartsWith("[TIMEOUT]") || error.Message.StartsWith("[SOCKET_EXCEPTION]")))
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
    
    public Result<PingResult> Ping(string? message = null) => PingAsync(message).GetAwaiter().GetResult();

    public async Task<Result<PingResult>> PingAsync(string? message = null, CancellationToken cancellationToken = default)
    {
        // Since we're sending the same ping message to all connections, we can expect a single identical response
        // from all nodes, hence why we're only storing a single result here. 
        var responseMessage = default(string);
        
        var okResponses = new List<string>();
        var errorResponses = new List<string>();

        foreach (var connection in this)
        {
            var endPoint = connection.EndPoint;
            
            var pingResult = await connection.PingAsync(message, cancellationToken);

            if (pingResult is Error<PingResult> err)
            {
                errorResponses.Add($"[Error] {endPoint.Address}:{endPoint.Port} error: {err.Message}");
            }
            else
            {
                var pingResponse = pingResult.Unwrap();

                if (responseMessage is null)
                {
                    responseMessage = pingResponse.Response;
                }
                
                okResponses.Add($"[OK] {endPoint.Address}:{endPoint.Port}: {pingResponse}");
            }
        }

        if (errorResponses.Count == 0)
        {
            return Result<PingResult>.Ok(new(responseMessage!));
        }

        return Result<PingResult>.Error($"PING FAILED:\n{string.Join("\n", okResponses.Union(errorResponses))}");
    }

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

            if (roleResult is Ok<RoleResult> { Value: PrimaryRoleResult } roleOk)
            {
                var slotsResult = connection.Slots();

                if (slotsResult is Error<ClusterSlots> slotsError)
                {
                    return Result<IRedisConnection>.Error("Couldn't get slots for the connection.", slotsError);
                }

                if (slotsResult is Ok<ClusterSlots> slotsOk && slotsOk.Value.ContainsSlot(key.Slot))
                {
                    Trace.WriteLine($"Key: {key.Name}, Slot: {key.Slot}, Connection: {connection.EndPoint}");
                    
                    return Result<IRedisConnection>.Ok(connection);
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

        Trace.WriteLine($"Getting connection for `{command.Command}` for key `{command.Keys.First().Name}` @ slot: {command.Keys.First().Slot}.");

        // Everything is in the same slot so just go get a node. 
        return GetNodeForKey(command.Keys.First());
    }
}