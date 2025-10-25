using System.Diagnostics;
using ReadUs.Commands;
using ReadUs.Commands.ResultModels;
using ReadUs.Errors;
using static ReadUs.StandardValues;
using static ReadUs.Extras.SocketTools;

namespace ReadUs;

// RedisClusterConnection is essentially just a collection of connections to the various
// redis cluster nodes. 
public class RedisClusterConnection : List<RedisConnection>, IRedisConnection
{
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

    public string ConnectionName => $"Redis Cluster Connection: {string.Join(";", this.Select(x => x.ConnectionName))}";
    public bool IsConnected => this.All(x => x.IsConnected);
    public bool IsFaulted { get; private set; }

    public Result<byte[]> SendCommand(RedisCommandEnvelope command)
    {
        var nodeResult = GetNodeForKeys(command).GetAwaiter().GetResult();

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

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        var nodeResult = await GetNodeForKeys(command, cancellationToken);

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

    private static bool IsResponseFaulted(Result<byte[]> response) =>
        response is Error<byte[]> { Details: CommandTimeout or SocketError };

    public async Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse, CancellationToken cancellationToken = default)
    {
        foreach (var connection in this)
        {
            await connection.SendCommandWithMultipleResponses(command, onResponse, cancellationToken);
        }
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

        if (await CheckHealth(cancellationToken))
        {
            return Result.Ok;
        }

        return Result.Error("Connection failed health check.");
    }

    private async Task<bool> CheckHealth(CancellationToken cancellationToken = default)
    {
        var slots = new HashSet<int>();

        foreach (var connection in this)
        {
            var slotsResult = await connection.Slots(cancellationToken);
            var ownedSlots = slotsResult.UnwrapOr(ClusterSlots.Default).OwnedSlots;

            slots.UnionWith(ownedSlots);
        }

        return slots.Count == MaxClusterSlots;
    }

    public void Dispose()
    {
        foreach (var connection in this)
        {
            connection.Dispose();
        }
    }

    private async Task<Result<IRedisConnection>> GetNodeForKey(RedisKey key, CancellationToken cancellationToken = default)
    {
        // We're not using a linq statement here because we're now executing commands against a (supposedly) open
        // connection, we need to be super deliberate about this. 
        foreach (var connection in this)
        {
            var roleResult = await connection.Role(cancellationToken);

            if (roleResult is Error<RoleResult> roleError)
            {
                return Result<IRedisConnection>.Error(roleError.Message);
            }

            if (roleResult is Ok<RoleResult> { Value: PrimaryRoleResult })
            {
                var slotsResult = await connection.Slots(cancellationToken);

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

    private async Task<Result<IRedisConnection>> GetNodeForKeys(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
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
        return await GetNodeForKey(command.Keys.First(), cancellationToken);
    }
}