using System.Net;
using System.Net.Sockets;

namespace ReadUs.Tests.Integration.Fixtures;

/// <summary>
/// Finds a free host TCP port, for the fixtures that run their containers under host
/// networking (<see cref="ClusterRedisFixture"/>, <see cref="SentinelRedisFixture"/>)
/// and so can't rely on Testcontainers' usual bridge-networking random-port mapping.
/// </summary>
internal static class FreePort
{
    public static int Find()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
