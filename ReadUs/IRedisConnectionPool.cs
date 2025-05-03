using System;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    Task<IRedisDatabase> GetAsync(); // TODO: Rename this to `GetDatabase`.

    // Task<IRedisConnection> GetConnection();
    //
    // void ReturnConnection(IRedisConnection connection);
}