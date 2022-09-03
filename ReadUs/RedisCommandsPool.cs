using System.Threading.Tasks;

namespace ReadUs
{
    public abstract class RedisCommandsPool : IRedisCommandsPool
    {
        public abstract Task<IRedisDatabase> GetAsync();

        public abstract void ReturnConnection(IRedisConnection connection);

        public abstract void Dispose();
    }
}