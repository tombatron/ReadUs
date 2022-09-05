using System.Linq;
using static ReadUs.Encoder.Encoder;

namespace ReadUs
{
    public readonly struct RedisCommandEnvelope
    {
        public RedisKey[] Keys { get; }

        public byte[] RawCommand { get; }

        public RedisCommandEnvelope(string[] keys, params object[] items)
        {
            Keys = keys.Select(x => new RedisKey(x)).ToArray();

            RawCommand = Encode(items);
        }
    }
}