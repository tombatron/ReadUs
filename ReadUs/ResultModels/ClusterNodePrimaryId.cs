namespace ReadUs.ResultModels;

public sealed class ClusterNodePrimaryId
{
    private readonly string _primaryIdValue;

    private ClusterNodePrimaryId(string primaryIdValue)
    {
        _primaryIdValue = primaryIdValue;
    }

    public static implicit operator ClusterNodePrimaryId?(char[] rawValue)
    {
        if (rawValue[0] == '-') return default;

        return new ClusterNodePrimaryId(new string(rawValue));
    }

    public static implicit operator string?(ClusterNodePrimaryId value)
    {
        if (value?._primaryIdValue is null) return default;

        return value._primaryIdValue;
    }
}