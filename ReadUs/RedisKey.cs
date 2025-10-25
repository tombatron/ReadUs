using static ReadUs.RedisKeyUtilities;

namespace ReadUs;

public readonly struct RedisKey(string name)
{
    public string Name { get; } = name;

    public uint Slot { get; } = ComputeHashSlot(name);

    public static implicit operator RedisKey(string keyName) => new (keyName);

    internal RedisKey[] ToArray() => [this];
}