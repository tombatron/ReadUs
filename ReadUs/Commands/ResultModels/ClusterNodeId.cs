namespace ReadUs.Commands.ResultModels;

public sealed class ClusterNodeId
{
    internal readonly char[] _rawValue;

    internal ClusterNodeId(char[] rawValue)
    {
        _rawValue = rawValue;
    }

    public static implicit operator ClusterNodeId(char[] rawValue) => new(rawValue);

    public static implicit operator string(ClusterNodeId nodeId) => new string(nodeId._rawValue).Trim();

    public override bool Equals(object? obj) => obj is ClusterNodeId && GetHashCode() == obj.GetHashCode();

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 13;

            foreach (var c in _rawValue)
            {
                hash += hash * 7 + c;
            }

            return hash;
        }
    }
}