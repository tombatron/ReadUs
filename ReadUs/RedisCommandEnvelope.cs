using System;
using System.Collections.Generic;
using System.Linq;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public readonly struct RedisCommandEnvelope
    {
        public string? CommandName { get; }

        public string? SubCommandName { get; }

        public RedisKey[]? Keys { get; }

        public object[]? Items { get; }

        public TimeSpan Timeout { get; }

        public RedisCommandEnvelope(string? commandName, string? subCommandName, RedisKey[]? keys, TimeSpan? timeout, params object[]? items)
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

            var combinedParameters = CombineParameters(availableParameters);

            return Encode(combinedParameters);
        }

        public static implicit operator byte[](RedisCommandEnvelope envelope) => envelope.ToByteArray();

        public static implicit operator ReadOnlyMemory<byte>(RedisCommandEnvelope envelope) => envelope.ToByteArray();

        public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
            new RedisCommandEnvelope(Client, ClientSubcommands.SetName, default, TimeSpan.FromSeconds(5), clientConnectionName);

        public static RedisCommandEnvelope CreateSetMultipleCommand(KeyValuePair<RedisKey, string>[] keysAndValues) =>
            new RedisCommandEnvelope(SetMultiple, default, keysAndValues.Keys(), default, keysAndValues);

        public static RedisCommandEnvelope CreateGetCommand(RedisKey key) =>
            new RedisCommandEnvelope(Get, default, new[] { key }, default, key);

        public static RedisCommandEnvelope CreateLeftPushCommand(RedisKey key, string[] elements) =>
            new RedisCommandEnvelope(LeftPush, default, new[] { key }, default, key, elements);

        public static RedisCommandEnvelope CreateListLengthCommand(RedisKey key) =>
            new RedisCommandEnvelope(ListLength, default, new[] { key }, default, key);

        public static RedisCommandEnvelope CreateRightPushCommand(RedisKey key, string[] elements) =>
            new RedisCommandEnvelope(RightPush, default, new[] { key }, default, key, elements);
    }
}