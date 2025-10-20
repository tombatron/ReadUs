using System.Globalization;
using System.Web;
using ReadUs.Commands.ResultModels;
using ReadUs.Exceptions;

namespace ReadUs;

public readonly struct RedisConnectionConfiguration(
    string serverAddress,
    int serverPort = RedisConnectionConfiguration.DefaultRedisPort,
    int connectionsPerNode = 1)
{
    private const string RedisScheme = "redis";
    private const int DefaultRedisPort = 6379;
    private const string ConnectionsPerNodeKey = "connectionsPerNode";

    public string ServerAddress { get; } = serverAddress;

    public int ServerPort { get; } = serverPort;

    // TODO: I don't even remember what I was thinking about here. 
    public int ConnectionsPerNode { get; } = connectionsPerNode;

    public static implicit operator RedisConnectionConfiguration(Uri connectionString)
    {
        if (string.Compare(RedisScheme, connectionString.Scheme, false, CultureInfo.InvariantCulture) != 0)
        {
            throw new RedisConnectionConfigurationException($"The provided scheme `{connectionString.Scheme}` is invalid, it must be `{RedisScheme}`.");
        }
        var host = connectionString.DnsSafeHost;
        var port = connectionString.Port == 0 ? DefaultRedisPort : connectionString.Port;

        var queryEntries = HttpUtility.ParseQueryString(connectionString.Query);

        if (int.TryParse(queryEntries.Get(ConnectionsPerNodeKey) ?? "1", out var parsedConnectionsPerNode))
        {
            return new RedisConnectionConfiguration(host, port, parsedConnectionsPerNode);
        }

        throw new RedisConnectionConfigurationException("`connectionsPerNode` must be a valid integer.");
    }

    public static implicit operator RedisConnectionConfiguration(ClusterNodesResultItem clusterNodesResultItem) =>
        new(clusterNodesResultItem.Address!.IpAddress.ToString(), clusterNodesResultItem.Address.RedisPort, 1);
}