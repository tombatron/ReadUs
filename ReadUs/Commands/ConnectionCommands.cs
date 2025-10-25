using ReadUs.Commands.ResultModels;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;
using SlotRange = ReadUs.Commands.ResultModels.ClusterSlots.SlotRange;

namespace ReadUs.Commands;

public static partial class Commands
{
    public static async Task<Result<RoleResult>> Role(this IRedisConnection @this, CancellationToken cancellationToken = default)
    {
        if (@this.IsConnected)
        {
            var result = await @this.SendCommandAsync(CreateRoleCommand(), cancellationToken);

            if (result is Ok<byte[]> ok)
            {
                var parseResult = Parse(ok.Value);

                if (parseResult is Error<ParseResult> parseErr)
                {
                    return Result<RoleResult>.Error(parseErr.Message);
                }

                return Result<RoleResult>.Ok((RoleResult)parseResult.Unwrap());
            }

            if (result is Error<byte[]> err)
            {
                return Result<RoleResult>.Error(err.Message);
            }
        }

        return Result<RoleResult>.Error("Socket isn't ready, can't execute command.");
    }

    public static Result<RoleResult> RoleSync(this IRedisConnection @this) => @this.Role().GetAwaiter().GetResult();

    private static readonly Result<ClusterSlots> DefaultSlots = Result<ClusterSlots>.Ok(new ClusterSlots(new SlotRange(0, 16_384)));

    public static async Task<Result<ClusterSlots>> Slots(this RedisConnection @this, CancellationToken cancellationToken = default)
    {
        if (@this.IsConnected)
        {
            var result = await @this.SendCommandAsync(CreateClusterShardsCommand(), cancellationToken);

            if (result is Error<byte[]> err)
            {
                return Result<ClusterSlots>.Error("Error getting slots for connection.", err);
            }

            var commandResult = result.Unwrap();
            var parsedResult = Parse(commandResult);

            if (parsedResult is Error<ParseResult> parseErr)
            {
                return Result<ClusterSlots>.Error("Error parsing the command result.", parseErr);
            }

            var okResult = parsedResult.Unwrap();

            if (IsNotCluster(okResult))
            {
                return DefaultSlots;
            }

            var clusterShards = new ClusterShardsResult(okResult);
            var currentShard = clusterShards.First(x =>
                x.Nodes!.Any(y => y.Port == @this.EndPoint.Port && y.Ip.Equals(@this.EndPoint.Address)));
        }


        return Result<ClusterSlots>.Error("Socket isn't ready, can't execute command.");
    }
    
    private const string NotAClusterError = "ERR This instance has cluster support disabled";
    private static bool IsNotCluster(ParseResult result) => (
        result.Type == ResultType.Error &&
        string.Compare(new string(result.Value), NotAClusterError, StringComparison.InvariantCultureIgnoreCase) == 0);
    
    public static async Task<Result<PingResult>> Ping(this IRedisConnection @this, string? message = null, CancellationToken cancellationToken = default)
    {
        if (@this.IsConnected)
        {
            var result = await @this.SendCommandAsync(CreatePingCommand(message), cancellationToken);

            if (result is Error<byte[]> err)
            {
                return Result<PingResult>.Error("Error executing ping command.", err);
            }

            var commandResult = result.Unwrap();
            var parsedResult = Parse(commandResult);

            if (parsedResult is Error<ParseResult> parseErr)
            {
                return Result<PingResult>.Error("There was an error parsing the ping command result.", parseErr);
            }
            
            var okResult = parsedResult.Unwrap();
            var pingResult = new PingResult(new string(okResult.Value));
            
            return Result<PingResult>.Ok(pingResult);
        }
        
        return Result<PingResult>.Error("Socket isn't ready, can't execute the `PING` command.");
    }

    public static async Task<Result> Select(this IRedisConnection @this, int databaseId, CancellationToken cancellationToken = default)
    {
        if (@this.IsConnected)
        {
            var result = await @this.SendCommandAsync(CreateSelectCommand(databaseId), cancellationToken);

            if (result is Error<byte[]> err)
            {
                return Result.Error("Error executing select command.", err);
            }
            
            result.Unwrap();
            
            return Result.Ok;
        }

        return Result.Error("Socket isn't ready, can't execute the `PING` command.");
    }
}