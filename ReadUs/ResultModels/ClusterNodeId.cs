namespace ReadUs.ResultModels
{
    public class ClusterNodeId
    {
        private readonly char[] _rawValue;

        private ClusterNodeId(char[] rawValue) =>
            _rawValue = rawValue;

        public static implicit operator ClusterNodeId(char[] rawValue) =>
            new ClusterNodeId(rawValue);

        public static implicit operator string(ClusterNodeId nodeId) =>
            new string(nodeId._rawValue);
    }
}