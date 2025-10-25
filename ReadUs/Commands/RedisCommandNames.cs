namespace ReadUs.Commands;

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

    internal const string Client = "CLIENT";

    internal const string Role = "ROLE";

    internal const string Ping = "PING";

    internal static class ClusterSubcommands
    {
        internal const string Nodes = "NODES";
        internal const string Shards = "SHARDS";
    }

    internal static class ClientSubcommands
    {
        internal const string SetName = "SETNAME";
    }

    internal static class PubSubCommands
    {
        internal const string Publish = "PUBLISH";
        
        internal const string Subscribe = "SUBSCRIBE";
        
        internal const string Unsubscribe = "UNSUBSCRIBE";

        internal const string PatternSubscribe = "PSUBSCRIBE";

        internal const string PatternUnsubscribe = "PUNSUBSCRIBE";
    }
}