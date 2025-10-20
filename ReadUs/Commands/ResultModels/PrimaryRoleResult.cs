using ReadUs.Parser;

namespace ReadUs.Commands.ResultModels;

public sealed class PrimaryRoleResult : RoleResult
{
    public PrimaryRoleResult(long currentReplicationOffset, ReplicaDescription[]? replicas)
    {
        CurrentReplicationOffset = currentReplicationOffset;
        Replicas = replicas;
    }

    public long CurrentReplicationOffset { get; }

    public ReplicaDescription[]? Replicas { get; }

    public static explicit operator PrimaryRoleResult(ParseResult[] result)
    {
        var currentReplicationOffset = long.Parse(result[1].ToString());

        ReplicaDescription[]? associatedReplicas = null;

        if (result[2].TryToArray(out var replicas) && replicas.Length > 0)
        {
            associatedReplicas = new ReplicaDescription[replicas.Length];

            for (var i = 0; i < replicas.Length; i++)
            {
                if (replicas[i].TryToArray(out var replica) && replica.Length == 3)
                {
                    associatedReplicas[i] = new ReplicaDescription(replica[0], replica[1], replica[2]);
                }
            }
        }

        return new PrimaryRoleResult(currentReplicationOffset, associatedReplicas);
    }
}