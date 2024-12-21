namespace ReadUs.ResultModels;

public sealed class ClusterNodeId
{
    internal readonly char[] _rawValue;

    internal ClusterNodeId(char[] rawValue) =>
        _rawValue = rawValue;

    public static implicit operator ClusterNodeId(char[] rawValue) =>
        new ClusterNodeId(rawValue);

    public static implicit operator string(ClusterNodeId nodeId) =>
        new string(nodeId._rawValue).Trim();

    public override bool Equals(object? obj)
    {
        if (obj is ClusterNodeId otherClusterNodeId)
        {
            return GetHashCode() == otherClusterNodeId.GetHashCode();
        }
        else
        {
            return false;
        }
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 13;

            foreach(var c in _rawValue)
            {
                hash += (hash * 7) + c;
            }

            return hash;
        }
    }
}