using System.Text;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;

namespace ReadUs;

public class RedisCommandEnvelope(string? command, string[]? subCommands, RedisKey[]? keys, bool simpleCommand, params object?[] items)
{
    public string? Command { get; } = command;

    public string[]? SubCommands { get; } = subCommands;

    public RedisKey[]? Keys { get; } = keys;

    public object?[]? Items { get; } = items;

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

    public byte[] ToByteArray()
    {
        byte[] bytes = SimpleCommand ? ToSimpleByteArray() : ToComplexByteArray();

        return bytes;
    }

    private byte[] ToSimpleByteArray()
    {
        var redisCommand = new StringBuilder();
        redisCommand.Append(Command);

        if (SubCommands is not null)
        {
            foreach (var subCommand in SubCommands)
            {
                redisCommand.Append(' ');
                redisCommand.Append(subCommand);
            }
        }

        if (Keys is not null)
        {
            foreach (var key in Keys)
            {
                redisCommand.Append(' ');
                redisCommand.Append(key); // Assuming RedisKey has proper ToString()
            }
        }

        if (Items is not null)
        {
            foreach (var item in Items)
            {
                redisCommand.Append(' ');
                redisCommand.Append(item);
            }
        }

        redisCommand.Append('\r');
        redisCommand.Append('\n');

        return Encoding.UTF8.GetBytes(redisCommand.ToString());
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

        if (Keys is not null)
        {
            availableParameters.AddRange(Keys.Select(k => k.Name));
        }

        if (Items is not null)
        {
            availableParameters.AddRange(Items!);
        }

        var combinedParameters = CombineParameters(availableParameters.ToArray());

        return Encode(combinedParameters);
    }

    public static implicit operator byte[](RedisCommandEnvelope envelope) => envelope.ToByteArray();

    public static implicit operator ReadOnlyMemory<byte>(RedisCommandEnvelope envelope) => envelope.ToByteArray();
}