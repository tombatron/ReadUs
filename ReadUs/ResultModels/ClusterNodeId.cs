namespace ReadUs.ResultModels;

public sealed class ClusterNodeId
{
    internal readonly char[] _rawValue;

    internal ClusterNodeId(char[] rawValue)
    {
        _rawValue = rawValue;
    }

    public static implicit operator ClusterNodeId(char[] rawValue)
    {
        return new ClusterNodeId(rawValue);
    }

    public static implicit operator string(ClusterNodeId nodeId)
    {
        return new string(nodeId._rawValue).Trim();
    }

    public override bool Equals(object? obj)
    {
        if (obj is ClusterNodeId otherClusterNodeId) return GetHashCode() == otherClusterNodeId.GetHashCode();

        return false;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 13;

            foreach (var c in _rawValue) hash += hash * 7 + c;

            return hash;
        }
    }
}