using System;
using System.Globalization;
using System.Web;
using ReadUs.Exceptions;

namespace ReadUs;

public readonly struct RedisConnectionConfiguration
{
    private const string RedisScheme = "redis";
    private const int DefaultRedisPort = 6379;
    private const string ConnectionsPerNodeKey = "connectionsPerNode";

    public string ServerAddress { get; }

    public int ServerPort { get; }

    public int ConnectionsPerNode { get; }

    public RedisConnectionConfiguration(string serverAddress, int serverPort = DefaultRedisPort, int connectionsPerNode = 1)
    {
        ServerAddress = serverAddress;
        ServerPort = serverPort;
        ConnectionsPerNode = connectionsPerNode;
    }

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
}