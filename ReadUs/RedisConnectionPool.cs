using System;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Exceptions;

namespace ReadUs;

public abstract class RedisConnectionPool : IRedisConnectionPool
{
    public abstract Task<IRedisDatabase> GetDatabase(int databaseId = 0, CancellationToken cancellationToken = default);

    internal abstract Task<IRedisConnection> GetConnection();

    internal abstract void ReturnConnection(IRedisConnection connection);

    public abstract void Dispose();

    public static IRedisConnectionPool Create(Uri connectionString)
    {
        Result<IRedisConnectionPool> newPool;

        RedisConnectionConfiguration configuration = connectionString;

        if (RedisClusterConnectionPool.TryGetClusterInformation(configuration, out var clusterNodesResult))
        {
            newPool = RedisClusterConnectionPool.Create(clusterNodesResult, configuration);
        }
        else
        {
            newPool = RedisSingleInstanceConnectionPool.Create(configuration);
        }

        if (newPool is Error<IRedisConnectionPool> errorPool)
        {
            throw new RedisConnectionException(errorPool.ToErrorString());
        }

        return newPool.Unwrap();
    }
}