using System;
using System.Threading.Tasks;

namespace ReadUs.Console;

public static class PubSubScenario
{
    public static async Task Run()
    {
        var connectionString = new Uri("redis://192.168.86.40:6379?connectionsPerNode=5");
        
        using var redis = RedisConnectionPool.Create(connectionString);
    }
}