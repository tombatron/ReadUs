using System;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    Task<IRedisDatabase> GetAsync();

    void ReturnConnection(IRedisConnection connection);

    IRedisSubscription CreateSubscriber();
}