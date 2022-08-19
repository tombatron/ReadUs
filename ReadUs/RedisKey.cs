using static ReadUs.RedisKeyUtilities;

namespace ReadUs
{
    public readonly struct RedisKey
    {
        public string Name { get; }

        public uint Slot { get; }

        public RedisKey(string name)
        {
            Name = name;

            Slot = ComputeHashSlot(name);
        }

        public static implicit operator RedisKey(string keyName) =>
            new RedisKey(keyName);
    }
}