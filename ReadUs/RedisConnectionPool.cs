using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs;

public abstract class RedisConnectionPool : IRedisConnectionPool
{
    public abstract Task<IRedisDatabase> GetDatabase(int databaseId = 0, CancellationToken cancellationToken = default);
    
    internal abstract Task<IRedisConnection> GetConnection();

    internal abstract void ReturnConnection(IRedisConnection connection);

    public abstract void Dispose();

    public static IRedisConnectionPool Create(Uri connectionString)
    {
        RedisConnectionConfiguration configuration = connectionString;

        // TODO: Sentinel connection pool
        if (RedisClusterConnectionPool.TryGetClusterInformation(configuration, out var clusterNodesResult))
        {
            return new RedisClusterConnectionPool(clusterNodesResult, configuration);
        }
        
        return new RedisSingleInstanceConnectionPool(configuration);
    }
}