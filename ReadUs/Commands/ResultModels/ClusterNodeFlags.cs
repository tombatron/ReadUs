using System.Collections.ObjectModel;

namespace ReadUs.Commands.ResultModels;

public class ClusterNodeFlags : ReadOnlyCollection<string>
{
    private const string PrimaryRoleFlag = "master";
    private const string SecondaryRoleFlag = "slave";

    private ClusterNodeFlags(IList<string> list) : base(list)
    {
    }

    private static ClusterNodeFlags FromCharArray(char[] rawValue)
    {
        var flagEntries = new string(rawValue).Split(",");

        return new ClusterNodeFlags(flagEntries);
    }

    public static implicit operator ClusterNodeFlags(char[] rawValue) => FromCharArray(rawValue);

    public static implicit operator ClusterNodeRole(ClusterNodeFlags flags)
    {
        if (flags.Contains(PrimaryRoleFlag))
        {
            return ClusterNodeRole.Primary;
        }

        if (flags.Contains(SecondaryRoleFlag))
        {
            return ClusterNodeRole.Secondary;
        }

        return ClusterNodeRole.Undefined;
    }

    public static implicit operator string(ClusterNodeFlags clusterNodeFlags) =>
        string.Join("|", clusterNodeFlags.ToArray());
}