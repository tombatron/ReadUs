using System;
using System.Net;

namespace ReadUs.ResultModels;

public class ClusterNodeAddress
{
    private readonly char[] _rawValue;

    private ClusterNodeAddress(char[] rawValue)
    {
        _rawValue = rawValue;

        var startIndex = 0;
        var nextDelimiter = Array.IndexOf(rawValue, ':', startIndex);

        IpAddress = IPAddress.Parse(rawValue[startIndex..nextDelimiter]);

        startIndex = nextDelimiter + 1;
        nextDelimiter = Array.IndexOf(rawValue, '@', startIndex);

        RedisPort = int.Parse(rawValue[startIndex..nextDelimiter]);

        startIndex = nextDelimiter + 1;

        ClusterPort = int.Parse(rawValue[startIndex..]);
    }

    internal ClusterNodeAddress(IPAddress ipAddress, int redisPort, int clusterPort)
    {
        IpAddress = ipAddress;
        RedisPort = redisPort;
        ClusterPort = clusterPort;

        _rawValue = $"{IpAddress}:{RedisPort}@{ClusterPort}".ToCharArray();
    }

    public IPAddress IpAddress { get; }

    public int RedisPort { get; }

    public int ClusterPort { get; }

    public static implicit operator ClusterNodeAddress(char[] rawValue)
    {
        return new ClusterNodeAddress(rawValue);
    }

    public static implicit operator string(ClusterNodeAddress clusterNodeAddress)
    {
        return new string(clusterNodeAddress._rawValue);
    }
}