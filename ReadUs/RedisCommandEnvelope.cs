using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;
using static ReadUs.RedisCommandNames;
using static ReadUs.StandardValues;

namespace ReadUs;

public readonly struct RedisCommandEnvelope(string? command, string[]? subCommands, RedisKey[]? keys, TimeSpan? timeout, bool simpleCommand, params object[]? items)
{
    public string? Command { get; } = command;

    public string[]? SubCommands { get; } = subCommands;

    public RedisKey[]? Keys { get; } = keys;

    public object[]? Items { get; } = items;

    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(5);

    public bool SimpleCommand { get; } = simpleCommand;

    public bool AllKeysInSingleSlot
    {
        get
        {
            if (Keys is null || Keys.Length == 0)
            {
                return false;
            }

            var firstKeySlot = Keys[0].Slot;

            return Keys.All(x => x.Slot == firstKeySlot);
        }
    }

    public RedisCommandEnvelope(string? command, string? subCommand, RedisKey[]? keys, TimeSpan? timeout, params object[]? items) :
        this(command, subCommand is null ? null : [subCommand], keys, timeout, false, items)
    {
    }

    public byte[] ToByteArray() =>
        SimpleCommand ? ToSimpleByteArray() : ToComplexByteArray();


    private byte[] ToSimpleByteArray()
    {
        // TODO: Throw exception here if CommandName is null.
        var result = new byte[Command!.Length + CarriageReturnLineFeed.Length];
        
        // TODO: Maybe for simple commands we can just hard code it?
        Array.Copy(Encoding.UTF8.GetBytes(Command!), result, Command!.Length);
        Array.Copy(CarriageReturnLineFeed, 0, result, Command!.Length, CarriageReturnLineFeed.Length);

        return result;
    }

    private byte[] ToComplexByteArray()
    {
        var availableParameters = new List<object>();

        if (Command is not null)
        {
            availableParameters.Add(Command);
        }

        if (SubCommands is not null)
        {
            availableParameters.AddRange(SubCommands);
        }

        if (Items is not null)
        {
            availableParameters.AddRange(Items);
        }

        var combinedParameters = CombineParameters(availableParameters.ToArray());

        return Encode(combinedParameters);
    }

    public static implicit operator byte[](RedisCommandEnvelope envelope) => envelope.ToByteArray();

    public static implicit operator ReadOnlyMemory<byte>(RedisCommandEnvelope envelope) => envelope.ToByteArray();

    public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
        new(Client, ClientSubcommands.SetName, null, TimeSpan.FromSeconds(5), clientConnectionName);

    public static RedisCommandEnvelope CreateClusterNodesCommand() =>
        new(Cluster, ClusterSubcommands.Nodes, null, TimeSpan.FromMilliseconds(5));

    public static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues) =>
        new(SetMultiple, null, keysAndValues.Keys(), null, keysAndValues);
    
    public static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, string[] elements) =>
        new(LeftPush, null, [key], null, key, elements);

    public static RedisCommandEnvelope CreateListLengthCommand(RedisKey key) =>
        new(ListLength, null, [key], null, key);

    public static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, string[] elements) =>
        new(RightPush, null, [key], null, key, elements);

    public static RedisCommandEnvelope CreateRoleCommand() =>
        new(Role, null, null, null, simpleCommand: true);
    
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
}