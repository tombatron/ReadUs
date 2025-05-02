using ReadUs.ResultModels;

namespace ReadUs;

public interface IRedisNodeConnection : IRedisConnection
{
    new ClusterNodeRole Role { get; }

    ClusterSlots? Slots { get; }
}