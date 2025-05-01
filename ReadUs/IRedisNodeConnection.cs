using ReadUs.ResultModels;

namespace ReadUs;

public interface IRedisNodeConnection : IRedisConnection
{
    ClusterSlots? Slots { get; }
}