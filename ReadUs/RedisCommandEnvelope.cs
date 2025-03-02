using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;
using static ReadUs.RedisCommandNames;
using static ReadUs.StandardValues;

namespace ReadUs;

public readonly struct RedisCommandEnvelope
{
    public string? CommandName { get; }

    public string? SubCommandName { get; }

    public RedisKey[]? Keys { get; }

    public object[]? Items { get; }

    public TimeSpan Timeout { get; }

    public bool SimpleCommand { get; }

    public bool AllKeysInSingleSlot
    {
        get
        {
            if (Keys is null || Keys.Length == 0) return false;

            var firstKeySlot = Keys[0].Slot;

            return Keys.All(x => x.Slot == firstKeySlot);
        }
    }

    public RedisCommandEnvelope(string? commandName, string? subCommandName, RedisKey[]? keys, TimeSpan? timeout, params object[]? items) :
        this(commandName, subCommandName, keys, timeout, false, items)
    {
    }

    public RedisCommandEnvelope(string? commandName, string? subCommandName, RedisKey[]? keys, TimeSpan? timeout, bool simpleCommand, params object[]? items)
    {
        CommandName = commandName;

        SubCommandName = subCommandName;

        Keys = keys;

        Items = items;

        Timeout = timeout ?? TimeSpan.FromSeconds(5);

        SimpleCommand = simpleCommand;
    }

    public byte[] ToByteArray() =>
        SimpleCommand ? ToSimpleByteArray() : ToComplexByteArray();


    private byte[] ToSimpleByteArray()
    {
        // TODO: Throw exception here if CommandName is null.
        var result = new byte[CommandName!.Length + CarriageReturnLineFeed.Length];
        
        // TODO: Maybe for simple commands we can just hard code it?
        Array.Copy(Encoding.UTF8.GetBytes(CommandName!), result, CommandName!.Length);
        Array.Copy(CarriageReturnLineFeed, 0, result, CommandName!.Length, CarriageReturnLineFeed.Length);

        return result;
    }

    private byte[] ToComplexByteArray()
    {
        var availableParameters = new List<object>();

        if (CommandName is not null)
        {
            availableParameters.Add(CommandName);
        }

        if (SubCommandName is not null)
        {
            availableParameters.Add(SubCommandName);
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

    public static RedisCommandEnvelope CreateSelectCommand(int databaseId) =>
        new(Select, null, null, TimeSpan.FromSeconds(5), databaseId);

    public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
        new(Client, ClientSubcommands.SetName, null, TimeSpan.FromSeconds(5), clientConnectionName);

    public static RedisCommandEnvelope CreateClusterNodesCommand() =>
        new(Cluster, ClusterSubcommands.Nodes, null, TimeSpan.FromMilliseconds(5));

    public static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues) =>
        new(SetMultiple, null, keysAndValues.Keys(), null, keysAndValues);

    public static RedisCommandEnvelope CreateGetCommand(RedisKey key) =>
        new(Get, null, [key], null, key);

    public static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, string[] elements) =>
        new(LeftPush, null, [key], null, key, elements);

    public static RedisCommandEnvelope CreateListLengthCommand(RedisKey key) =>
        new(ListLength, null, [key], null, key);

    public static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, string[] elements) =>
        new(RightPush, null, [key], null, key, elements);

    public static RedisCommandEnvelope CreateSetCommand(RedisKey key, string value) =>
        new(Set, null, [key], null, key, value);

    public static RedisCommandEnvelope CreateBlockingLeftPopCommand(RedisKey[] keys, TimeSpan timeout) =>
        new(BlockingLeftPop, null, keys, timeout, keys);

    public static RedisCommandEnvelope CreateBlockingRightPopCommand(RedisKey[] keys, TimeSpan timeout) =>
        new(BlockingRightPop, null, keys, timeout, keys);

    public static RedisCommandEnvelope CreateRoleCommand() =>
        new(Role, null, null, null, simpleCommand: true);
}