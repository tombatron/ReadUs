using ReadUs.ResultModels;

namespace ReadUs
{
    public interface IRedisNodeConnection : IRedisConnection
    {
        ClusterNodeRole Role { get; }

        ClusterSlots? Slots { get; }
    }
}
