using System;
using System.Collections.Generic;
using System.Linq;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;
using static ReadUs.RedisCommandNames;

namespace ReadUs;

public readonly struct RedisCommandEnvelope
{
    public string? CommandName { get; }

    public string? SubCommandName { get; }

    public RedisKey[]? Keys { get; }

    public object[]? Items { get; }

    public TimeSpan Timeout { get; }

    public bool AllKeysInSingleSlot
    {
        get
        {
            if (Keys is null || Keys.Length == 0) return false;

            var firstKeySlot = Keys[0].Slot;

            return Keys.All(x => x.Slot == firstKeySlot);
        }
    }

    public RedisCommandEnvelope(string? commandName, string? subCommandName, RedisKey[]? keys, TimeSpan? timeout,
        params object[]? items)
    {
        CommandName = commandName;

        SubCommandName = subCommandName;

        Keys = keys;

        Items = items;

        Timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    public byte[] ToByteArray()
    {
        var availableParameters = new List<object>();

        if (CommandName is not null) availableParameters.Add(CommandName);

        if (SubCommandName is not null) availableParameters.Add(SubCommandName);

        if (Items is not null) availableParameters.AddRange(Items);

        var combinedParameters = CombineParameters(availableParameters.ToArray());

        return Encode(combinedParameters);
    }

    public static implicit operator byte[](RedisCommandEnvelope envelope)
    {
        return envelope.ToByteArray();
    }

    public static implicit operator ReadOnlyMemory<byte>(RedisCommandEnvelope envelope)
    {
        return envelope.ToByteArray();
    }

    public static RedisCommandEnvelope CreateSelectCommand(int databaseId)
    {
        return new RedisCommandEnvelope(Select, default, default, TimeSpan.FromSeconds(5), databaseId);
    }

    public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName)
    {
        return new RedisCommandEnvelope(Client, ClientSubcommands.SetName, default, TimeSpan.FromSeconds(5),
            clientConnectionName);
    }

    public static RedisCommandEnvelope CreateClusterNodesCommand()
    {
        return new RedisCommandEnvelope(Cluster, ClusterSubcommands.Nodes, default, TimeSpan.FromMilliseconds(5),
            default);
    }

    public static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues)
    {
        return new RedisCommandEnvelope(SetMultiple, default, keysAndValues.Keys(), default, keysAndValues);
    }

    public static RedisCommandEnvelope CreateGetCommand(RedisKey key)
    {
        return new RedisCommandEnvelope(Get, default, new[] { key }, default, key);
    }

    public static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, string[] elements)
    {
        return new RedisCommandEnvelope(LeftPush, default, new[] { key }, default, key, elements);
    }

    public static RedisCommandEnvelope CreateListLengthCommand(RedisKey key)
    {
        return new RedisCommandEnvelope(ListLength, default, new[] { key }, default, key);
    }

    public static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, string[] elements)
    {
        return new RedisCommandEnvelope(RightPush, default, new[] { key }, default, key, elements);
    }

    public static RedisCommandEnvelope CreateSetCommand(RedisKey key, string value)
    {
        return new RedisCommandEnvelope(Set, default, new[] { key }, default, key, value);
    }

    public static RedisCommandEnvelope CreateBlockingLeftPopCommand(RedisKey[] keys, TimeSpan timeout)
    {
        return new RedisCommandEnvelope(BlockingLeftPop, default, keys, timeout, keys);
    }

    public static RedisCommandEnvelope CreateBlockingRightPopCommand(RedisKey[] keys, TimeSpan timeout)
    {
        return new RedisCommandEnvelope(BlockingRightPop, default, keys, timeout, keys);
    }

    public static RedisCommandEnvelope CreateRoleCommand()
    {
        return new RedisCommandEnvelope(Role, default, default, default);
    }
}