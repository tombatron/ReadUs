using Xunit;

namespace ReadUs.Tests
{
    public class RedisKeyUtilitiesTests
    {
        [Theory]
        [InlineData("test_key", 15_118)]
        [InlineData("hello world", 15_332)]
        [InlineData("{hashtag}0983u4f098e4r0982j0948", 13_784)]
        [InlineData("{hashtag}this is a mundane key", 13_784)]
        [InlineData("goodbye", 9_354)]
        public void ItCanComputeHashSlot(string key, uint expectedSlot)
        {
            var computedSlot = RedisKeyUtilities.ComputeHashSlot(key);

            Assert.Equal(expectedSlot, computedSlot);
        }
    }
}