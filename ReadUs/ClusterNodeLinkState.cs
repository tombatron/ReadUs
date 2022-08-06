namespace ReadUs
{
    public abstract class ClusterNodeLinkState
    {
        private static readonly ClusterNodeLinkStateConnected _connected = new ClusterNodeLinkStateConnected();
        private static readonly ClusterNodeLinkStateDisconnected _disconnected = new ClusterNodeLinkStateDisconnected();
        
        public static implicit operator ClusterNodeLinkState(char[] rawValue)
        {
            if (rawValue[0] == 'c')
            {
                return _connected;
            }

            if (rawValue[0] == 'd')
            {
                return _disconnected;
            }
            
            return default;
        }
    }

    public sealed class ClusterNodeLinkStateConnected : ClusterNodeLinkState
    {
    }

    public sealed class ClusterNodeLinkStateDisconnected : ClusterNodeLinkState
    {
    }
}