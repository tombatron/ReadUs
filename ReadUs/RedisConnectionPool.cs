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
        IRedisConnectionPool newPool;

        RedisConnectionConfiguration configuration = connectionString;

        if (RedisClusterConnectionPool.TryGetClusterInformation(configuration, out var clusterNodesResult))
        {
            newPool = new RedisClusterConnectionPool(clusterNodesResult, configuration);
        }
        else
        {
            newPool = new RedisSingleInstanceConnectionPool(configuration);
        }
        
        

        return newPool;
    }
}