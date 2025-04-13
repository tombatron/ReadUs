using System;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    Task<IRedisDatabase> GetAsync();

    Task<IRedisConnection> GetConnection();

    void ReturnConnection(IRedisConnection connection);
}