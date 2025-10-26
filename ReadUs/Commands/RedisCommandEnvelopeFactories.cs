using ReadUs.Extras;
using static ReadUs.Commands.RedisCommandNames;

namespace ReadUs.Commands;

public static partial class Commands
{
    public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
        new(Client, [ClientSubcommands.SetName], null, true, clientConnectionName);

    public static RedisCommandEnvelope CreateClusterNodesCommand() =>
        new(Cluster, [ClusterSubcommands.Nodes], null, false);

    public static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues) =>
        new(RedisCommandNames.SetMultiple, null, keysAndValues.Keys(), false, keysAndValues);

    public static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, string[] elements) =>
        new(RedisCommandNames.LeftPush, null, [key], false, key, elements);

    public static RedisCommandEnvelope CreateListLengthCommand(RedisKey key) =>
        new(RedisCommandNames.ListLength, null, [key], false);

    public static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, string[] elements) =>
        new(RightPush, null, [key], false, elements);

    public static RedisCommandEnvelope CreateRoleCommand() =>
        new(RedisCommandNames.Role, null, null, simpleCommand: true);

    public static RedisCommandEnvelope CreateClusterShardsCommand() =>
        new(Cluster, [ClusterSubcommands.Shards], null, false);

    public static RedisCommandEnvelope CreatePublishCommand(string channel, string message) =>
        new(PubSubCommands.Publish, [channel], null, false, message);

    public static RedisCommandEnvelope CreateSubscribeCommand(string[] channels) =>
        new(PubSubCommands.Subscribe, channels, null, false);

    public static RedisCommandEnvelope CreatePatternSubscribeCommand(string[] channelPatterns) =>
        new(PubSubCommands.PatternSubscribe, channelPatterns, null, false);

    public static RedisCommandEnvelope CreateUnsubscribeCommand(string[] channels) =>
        new(PubSubCommands.Unsubscribe, channels, null, false);

    public static RedisCommandEnvelope CreatePatternUnsubscribeCommand(string[] channelPatterns) =>
        new(PubSubCommands.PatternUnsubscribe, channelPatterns, null, false);

    public static RedisCommandEnvelope CreatePingCommand(string? message) =>
        new(RedisCommandNames.Ping, null, null, true, message);

    public static RedisCommandEnvelope CreateSelectCommand(int databaseId) =>
        new(RedisCommandNames.Select, null, null, false, databaseId);
}