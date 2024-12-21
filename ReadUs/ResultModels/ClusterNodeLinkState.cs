namespace ReadUs.ResultModels;

public abstract class ClusterNodeLinkState
{
    private static readonly ClusterNodeLinkStateConnected _connected = new ClusterNodeLinkStateConnected();
    private static readonly ClusterNodeLinkStateDisconnected _disconnected = new ClusterNodeLinkStateDisconnected();
        
    public static implicit operator ClusterNodeLinkState(char[] rawValue) =>
        rawValue[0] switch
        {
            'c' => _connected,
            'd' => _disconnected,
            _ => _disconnected
        };
}

public sealed class ClusterNodeLinkStateConnected : ClusterNodeLinkState
{
}

public sealed class ClusterNodeLinkStateDisconnected : ClusterNodeLinkState
{
}