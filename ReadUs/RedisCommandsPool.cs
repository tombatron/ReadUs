using System.Threading.Tasks;

namespace ReadUs
{
    public abstract class RedisCommandsPool : IRedisConnectionPool
    {
        public abstract Task<IRedisDatabase> GetAsync();

        public abstract void ReturnConnection(IRedisConnection connection);

        public abstract void Dispose();
    }
}