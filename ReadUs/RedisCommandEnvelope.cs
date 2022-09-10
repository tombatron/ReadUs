using System;
using System.Collections.Generic;
using System.Linq;
using static ReadUs.Encoder.Encoder;
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

        public RedisCommandEnvelope(string? commandName, string? subCommandName, string[]? keys, TimeSpan timeout, params object[]? items)
        {
            CommandName = commandName;

            SubCommandName = subCommandName;

            Keys = keys.Select(x => new RedisKey(x)).ToArray();

            Items = items;

            Timeout = timeout;
        }

        public byte[]? ToByteArray()
        {
            var commandParts = new List<object>();

            if (CommandName is not null)
            {
                commandParts.Add(CommandName);
            }

            if (SubCommandName is not null)
            {
                commandParts.Add(SubCommandName);
            }

            if (Items is not null)
            {
                commandParts.AddRange(Items);
            }

            return Encode(commandParts);
        }

        public static implicit operator byte[]?(RedisCommandEnvelope envelope) => envelope.ToByteArray();

        public static RedisCommandEnvelope CreateClientSetNameCommand(string clientConnectionName) =>
            new RedisCommandEnvelope(Client, ClientSubcommands.SetName, null, TimeSpan.FromSeconds(5), clientConnectionName);


    }
}