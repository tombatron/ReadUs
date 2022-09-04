using ReadUs.ResultModels;

namespace ReadUs
{
    public sealed class RedisNodeConnection : RedisConnection, IRedisNodeConnection
    {
        public RedisNodeConnection(ClusterNodesResultItem nodeDescription) : base(nodeDescription.Address.IpAddress, nodeDescription.Address.RedisPort)
        {
            // TODO: For now we're just going to assume that the data returned by the cluster nodes command
            //       is valid. Though we are going to want to throw in some logic to account for shifting
            //       roles, additional nodes, changing slot assignments etc.
            Role = nodeDescription.Flags;
            Slots = nodeDescription.Slots;
        }

        public ClusterNodeRole Role { get; }

        public ClusterSlots Slots { get; }
    }
}