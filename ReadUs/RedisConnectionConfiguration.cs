using System.Globalization;
using System.Web;
using ReadUs.Exceptions;

namespace ReadUs;

public readonly struct RedisConnectionConfiguration(string serverAddress, int serverPort, string connectionName)
{
    private const string RedisScheme = "redis";
    private const int DefaultRedisPort = 6379;
    private const string ConnectionNameKey = "connectionName";

    public string ServerAddress => serverAddress;

    public int ServerPort => serverPort;

    public string ConnectionName => connectionName;

    public static implicit operator RedisConnectionConfiguration(Uri connectionString)
    {
        if (string.Compare(RedisScheme, connectionString.Scheme, false, CultureInfo.InvariantCulture) != 0)
        {
            throw new RedisConnectionConfigurationException($"The provided scheme `{connectionString.Scheme}` is invalid, it must be `{RedisScheme}`.");
        }
        
        var host = connectionString.DnsSafeHost;
        var port = connectionString.Port == 0 ? DefaultRedisPort : connectionString.Port;

        var queryEntries = HttpUtility.ParseQueryString(connectionString.Query);

        var connectionName = queryEntries.Get(ConnectionNameKey) ?? "ReadUs_Connection";
        
        return new RedisConnectionConfiguration(host, port, connectionName);
    }
}