using ReadUs.Extras;
using static ReadUs.Commands.RedisCommandNames;

namespace ReadUs.Commands;

public static partial class Commands
{
    private static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
        CommandBuilder
            .WithCommand(Client)
            .WithSubCommand(ClientSubcommands.SetName)
            .AddItem(clientConnectionName)
            .AsInlineCommand()
            .Build();

    private static RedisCommandEnvelope CreateClusterNodesCommand() =>
        CommandBuilder
            .WithCommand(Cluster)
            .WithSubCommand(ClusterSubcommands.Nodes)
            .Build();

    private static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues) =>
        CommandBuilder
            .WithCommand(RedisCommandNames.SetMultiple)
            .WithKeys(keysAndValues.Keys()!)
            .AddItems(keysAndValues)
            .Build();   

    private static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, object?[] elements) =>
        CommandBuilder
            .WithCommand(RedisCommandNames.LeftPush)
            .WithKey(key)
            .AddItems(elements)
            .Build();

    private static RedisCommandEnvelope CreateListLengthCommand(RedisKey key) =>
        CommandBuilder
            .WithCommand(RedisCommandNames.ListLength)
            .WithKey(key)
            .Build();

    private static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, object?[] elements) =>
        CommandBuilder
            .WithCommand(RightPush)
            .WithKey(key)
            .AddItems(elements)
            .Build();   

    private static RedisCommandEnvelope CreateRoleCommand() =>
        CommandBuilder
            .WithCommand(RedisCommandNames.Role)
            .AsInlineCommand()
            .Build();   

    private static RedisCommandEnvelope CreateClusterShardsCommand() =>
        CommandBuilder
            .WithCommand(Cluster)
            .WithSubCommand(ClusterSubcommands.Shards)
            .Build();

    private static RedisCommandEnvelope CreatePublishCommand(string channel, string message) =>
        CommandBuilder
            .WithCommand(PubSubCommands.Publish)
            .WithSubCommand(channel)
            .AddItem(message)
            .Build();

    internal static RedisCommandEnvelope CreateSubscribeCommand(string[] channels) =>
        CommandBuilder
            .WithCommand(PubSubCommands.Subscribe)
            .WithSubCommands(channels)
            .Build();

    internal static RedisCommandEnvelope CreatePatternSubscribeCommand(string[] channelPatterns) =>
        CommandBuilder
            .WithCommand(PubSubCommands.PatternSubscribe)
            .WithSubCommands(channelPatterns)
            .Build();

    private static RedisCommandEnvelope CreateUnsubscribeCommand(string[] channels) =>
        CommandBuilder
            .WithCommand(PubSubCommands.Unsubscribe)
            .WithSubCommands(channels)
            .Build();

    private static RedisCommandEnvelope CreatePatternUnsubscribeCommand(string[] channelPatterns) =>
        CommandBuilder
            .WithCommand(PubSubCommands.PatternUnsubscribe)
            .WithSubCommands(channelPatterns)
            .Build();

    private static RedisCommandEnvelope CreatePingCommand(string? message) =>
        CommandBuilder
            .WithCommand(RedisCommandNames.Ping)
            .AddItem(message)
            .AsInlineCommand()
            .Build();   

    private static RedisCommandEnvelope CreateSelectCommand(int databaseId) =>
        CommandBuilder
            .WithCommand(RedisCommandNames.Select)
            .AddItem(databaseId)
            .AsInlineCommand()
            .Build();
}