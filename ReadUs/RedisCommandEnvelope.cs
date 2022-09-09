using System;
using System.Linq;
using static ReadUs.Encoder.Encoder;

namespace ReadUs
{
    public readonly struct RedisCommandEnvelope
    {
        public RedisKey[] Keys { get; }

        public byte[] RawCommand { get; }

        public TimeSpan Timeout { get; }

        public RedisCommandEnvelope(string key, params object[] items) :
            this(new[] { key }, items)
        { }

        public RedisCommandEnvelope(string[] keys, params object[] items) :
            this(keys, TimeSpan.MaxValue, items)
        { }

        public RedisCommandEnvelope(string[] keys, TimeSpan timeout, params object[] items)
        {
            Keys = keys.Select(x => new RedisKey(x)).ToArray();

            RawCommand = Encode(items);

            Timeout = timeout;
        }
    }
}