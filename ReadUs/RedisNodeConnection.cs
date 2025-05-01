using System;
using ReadUs.ResultModels;

namespace ReadUs;

public sealed class RedisNodeConnection : RedisConnection, IRedisNodeConnection
{
    public RedisNodeConnection(ClusterNodesResultItem nodeDescription) :
        base(
            nodeDescription?.Address?.IpAddress ?? throw new Exception("IP Address cannot be null."),
            nodeDescription?.Address?.RedisPort ?? throw new Exception("Port cannot be null.")
        )
    {
        // TODO: For now we're just going to assume that the data returned by the cluster nodes command
        //       is valid. Though we are going to want to throw in some logic to account for shifting
        //       roles, additional nodes, changing slot assignments etc.
        if (nodeDescription.Flags is null)
        {
            // TODO: New custom exception needed here.
            throw new Exception("Didn't get any flag for the node connection.");
        }
        else
        {
            SetRole(nodeDescription.Flags);
        }

        Slots = nodeDescription.Slots;
    }

    public ClusterSlots? Slots { get; }
}