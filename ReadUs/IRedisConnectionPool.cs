using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs;

public interface IRedisConnectionPool : IDisposable
{
    Task<IRedisDatabase> GetDatabase(CancellationToken cancellationToken = default);
}