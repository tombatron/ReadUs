using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    Task<IRedisDatabase> GetDatabase(int databaseId, CancellationToken cancellationToken = default);
}