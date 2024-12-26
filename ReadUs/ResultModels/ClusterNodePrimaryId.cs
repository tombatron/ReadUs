namespace ReadUs.ResultModels;

public sealed class ClusterNodePrimaryId
{
    private readonly string _primaryIdValue;

    private ClusterNodePrimaryId(string primaryIdValue)
    {
        _primaryIdValue = primaryIdValue;
    }

    public static implicit operator ClusterNodePrimaryId?(char[] rawValue) => 
        rawValue[0] == '-' ? null : new ClusterNodePrimaryId(new string(rawValue));

    public static implicit operator string?(ClusterNodePrimaryId value) => value?._primaryIdValue;
}