using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReadUs
{
    // Let's make RedisCommandsPool an abstract class and essentially move all of this stuff into a
    // new class called RedisSingleInstanceCommandsPool. 
    // 
    // We'll 

    public abstract class RedisCommandsPool : IRedisConnectionPool
    {
        protected readonly string _serverAddress;
        protected readonly int _serverPort;

        public RedisCommandsPool(string serverAddress, int serverPort)
        {
            _serverAddress = serverAddress;
            _serverPort = serverPort;
        }

        public abstract Task<IRedisDatabase> GetAsync();

        public abstract void ReturnConnection(IRedisConnection connection);

        public abstract void Dispose();
    }
}