using static ReadUs.Commands.RedisCommandNames;

namespace ReadUs.Commands;

public static partial class Commands
{
        public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
        new(Client, ClientSubcommands.SetName, null, TimeSpan.FromSeconds(5), clientConnectionName);

    public static RedisCommandEnvelope CreateClusterNodesCommand() =>
        new(Cluster, ClusterSubcommands.Nodes, null, TimeSpan.FromMilliseconds(5));

    public static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues) =>
        new(RedisCommandNames.SetMultiple, null, keysAndValues.Keys(), null, keysAndValues);
    
    public static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, string[] elements) =>
        new(RedisCommandNames.LeftPush, null, [key], null, key, elements);

    public static RedisCommandEnvelope CreateListLengthCommand(RedisKey key) =>
        new(RedisCommandNames.ListLength, null, [key], null, key);

    public static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, string[] elements) =>
        new(RightPush, null, [key], null, key, elements);

    public static RedisCommandEnvelope CreateRoleCommand() =>
        new(RedisCommandNames.Role, null, null, null, simpleCommand: true);
    
    public static RedisCommandEnvelope CreateClusterShardsCommand() =>
        new(Cluster, ClusterSubcommands.Shards, null, TimeSpan.FromMilliseconds(5));

    public static RedisCommandEnvelope CreatePublishCommand(string channel, string message) =>
        new(PubSubCommands.Publish, channel, null, null, message);

    public static RedisCommandEnvelope CreateSubscribeCommand(string[] channels) =>
        new(PubSubCommands.Subscribe, channels, null, null, false);

    public static RedisCommandEnvelope CreatePatternSubscribeCommand(string[] channelPatterns) =>
        new(PubSubCommands.PatternSubscribe, channelPatterns, null, null, false);

    public static RedisCommandEnvelope CreateUnsubscribeCommand(string[] channels) =>
        new(PubSubCommands.Unsubscribe, channels, null, null, false);

    public static RedisCommandEnvelope CreatePatternUnsubscribeCommand(string[] channelPatterns) =>
        new(PubSubCommands.PatternUnsubscribe, channelPatterns, null, null, false);    
    
    public static RedisCommandEnvelope CreatePingCommand(string? message) =>
        new(RedisCommandNames.Ping,null, null, null, true, message);

    public static RedisCommandEnvelope CreateSelectCommand(int databaseId) =>
        new(RedisCommandNames.Select, null, null, TimeSpan.FromMilliseconds(10), databaseId);
}