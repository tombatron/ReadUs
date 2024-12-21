using System;
using System.Threading.Tasks;

namespace ReadUs;

public abstract class RedisConnectionPool : IRedisConnectionPool
{
    public abstract Task<IRedisDatabase> GetAsync();

    public abstract void ReturnConnection(IRedisConnection connection);

    public abstract void Dispose();

    public static IRedisConnectionPool Create(Uri connectionString)
    {
        RedisConnectionConfiguration configuration = connectionString;
    
        // TODO: Sentinel connection pool?
            
        if (RedisClusterConnectionPool.TryGetClusterInformation(configuration, out var clusterNodesResult))
        {
            return new RedisClusterConnectionPool(clusterNodesResult, configuration);
        }
        else
        {
            return new RedisSingleInstanceConnectionPool(configuration);
        }
    }
}