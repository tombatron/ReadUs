namespace ReadUs
{
    internal static class RedisCommandNames
    {
        internal const string Select = "SELECT";

        internal const string Get = "GET";

        internal const string Set = "SET";

        internal const string BlockingLeftPop = "BLPOP";

        internal const string BlockingRightPop = "BRPOP";

        internal const string LeftPush = "LPUSH";

        internal const string RightPush = "RPUSH";

        internal const string ListLength = "LLEN";

        internal const string SetMultiple = "MSET";

        internal const string Cluster = "CLUSTER";

        internal static class ClusterSubcommands
        {
            internal const string Nodes = "NODES";
        }

        internal const string Client = "CLIENT";

        internal static class ClientSubcommands
        {
            internal const string SetName = "SETNAME";
        }
    }
}
