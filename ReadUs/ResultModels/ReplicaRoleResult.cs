using System.Net;
using ReadUs.Parser;

namespace ReadUs.ResultModels;

public sealed class ReplicaRoleResult : RoleResult
{
    /// <summary>
    /// The IP address of the primary node that this replica is associated with.
    /// </summary>
    public IPAddress PrimaryAddress { get; }

    /// <summary>
    /// The port of the primary node that this replica is associated with.
    /// </summary>
    public int PrimaryPort { get; }

    /// <summary>
    /// The current state of replication between this replica and its primary.
    /// </summary>
    public ReplicationState ReplicationState { get; }

    /// <summary>
    /// The amount of data received from the replica so far in terms of primary replication.
    /// </summary>
    public long DataReceivedTotal { get; }

    public ReplicaRoleResult(IPAddress primaryAddress, int primaryPort, ReplicationState replicationState, long dataReceivedTotal)
    {
        PrimaryAddress = primaryAddress;
        PrimaryPort = primaryPort;
        ReplicationState = replicationState;
        DataReceivedTotal = dataReceivedTotal;
    }

    public static explicit operator ReplicaRoleResult(ParseResult[] result)
    {
        var primaryAddress = IPAddress.Parse(result[1].ToString());
        var primaryPort = int.Parse(result[2].ToString());

        var replicationState = result[3].ToString() switch
        {
            ReplicationStates.Connect => ReplicationState.Connect,
            ReplicationStates.Connecting => ReplicationState.Connecting,
            ReplicationStates.Sync => ReplicationState.Sync,
            ReplicationStates.Connected => ReplicationState.Connected,
            _ => ReplicationState.Connect
        };

        var dataReceivedTotal = long.Parse(result[4].ToString());

        return new ReplicaRoleResult(primaryAddress, primaryPort, replicationState, dataReceivedTotal);
    }
}