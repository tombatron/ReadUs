using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs
{
    public interface IRedisNodeConnection : IRedisConnection
    {
        ClusterNodeRole Role { get; }

        ClusterSlots Slots { get; }
    }
}
