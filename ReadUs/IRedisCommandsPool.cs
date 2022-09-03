using System;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisCommandsPool : IDisposable
    {
        Task<IRedisDatabase> GetAsync();

        void ReturnConnection(IRedisConnection connection);
    }
}