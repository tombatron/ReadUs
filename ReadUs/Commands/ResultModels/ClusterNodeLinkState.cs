namespace ReadUs.Commands.ResultModels;

public abstract class ClusterNodeLinkState
{
    private static readonly ClusterNodeLinkStateConnected Connected = new();
    private static readonly ClusterNodeLinkStateDisconnected Disconnected = new();

    public static implicit operator ClusterNodeLinkState(char[] rawValue)
    {
        return rawValue[0] switch
        {
            'c' => Connected,
            _ => Disconnected
        };
    }
}

public sealed class ClusterNodeLinkStateConnected : ClusterNodeLinkState
{
}

public sealed class ClusterNodeLinkStateDisconnected : ClusterNodeLinkState
{
}