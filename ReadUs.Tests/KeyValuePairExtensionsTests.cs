using System.Collections.Generic;
using Xunit;

namespace ReadUs.Tests
{
    public class KeyValuePairExtensionsTests
    {
        [Fact]
        public void IfInputIsNullOutputIsNull()
        {
            var nullArray = default(KeyValuePair<RedisKey, string>[]);

            var testResult = nullArray.Keys();

            Assert.Null(testResult);
        }

        [Fact]
        public void IfInputIsEmptyOutputIsEmpty()
        {
            var emptyArray = new KeyValuePair<RedisKey, string>[0];

            var testResult = emptyArray.Keys();

            Assert.Empty(testResult);
        }

        [Fact]
        public void ItWillReturnKeysFromAnArrayOfKeyValuePairs()
        {
            var testArray = new KeyValuePair<RedisKey, string>[2];

            testArray[0] = new KeyValuePair<RedisKey, string>("key1", "value1");
            testArray[1] = new KeyValuePair<RedisKey, string>("key2", "value2");

            var testResult = testArray.Keys();

            Assert.Equal("key1", testResult[0]);
            Assert.Equal("key2", testResult[1]);
        }
    }
}