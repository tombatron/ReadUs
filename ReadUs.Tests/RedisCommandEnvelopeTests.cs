using System.Linq;
using Xunit;
using static ReadUs.Encoder.Encoder;

namespace ReadUs.Tests
{
    public class RedisCommandEnvelopeTests
    {
        [Fact]
        public void ItWillCorrectlyInitializeKeys()
        {
            var keys = new string[] { "hello", "world", "goodnight", "moon" };

            var expectedKeys = keys.Select(x => new RedisKey(x)).ToArray();

            var commandEnvelope = new RedisCommandEnvelope(keys, null);

            Assert.Equal(expectedKeys, commandEnvelope.Keys);
        }

        [Fact]
        public void ItWillCorrectlyInitializeRawCommand()
        {
            var keys = new string[] { "hello" };

            var expectedRawCommand = Encode(new object[] { "world" });

            var commandEnvelope = new RedisCommandEnvelope(keys, "world");

            Assert.Equal(expectedRawCommand, commandEnvelope.RawCommand);
        }
    }
}