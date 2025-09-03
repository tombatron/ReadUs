using System.Collections.Generic;
using JetBrains.Annotations;
using Xunit;
using static ReadUs.ParameterUtilities;

namespace ReadUs.Tests;

[UsedImplicitly]
public class ParameterUtilitiesTests
{
    public class CombineParametersCan
    {
        [Fact]
        public void CombineSimpleItems()
        {
            var parameters = CombineParameters("johnny", 5, "is", "alive");

            Assert.Equal("johnny", parameters[0]);
            Assert.Equal(5, parameters[1]);
            Assert.Equal("is", parameters[2]);
            Assert.Equal("alive", parameters[3]);
        }

        [Fact]
        public void CombineItemsWithArray()
        {
            var simpleArray = new object[2];
            simpleArray[0] = 1;
            simpleArray[1] = 2;

            var parameters = CombineParameters("first", simpleArray, "second");

            Assert.Equal("first", parameters[0]);
            Assert.Equal(1, parameters[1]);
            Assert.Equal(2, parameters[2]);
            Assert.Equal("second", parameters[3]);
        }

        [Fact]
        public void CombineItemsWithRedisKeyStringValuePairs()
        {
            var simpleArray = new object[2];
            simpleArray[0] = 1;
            simpleArray[1] = 2;

            var redisKeyStringValuePairs = new KeyValuePair<RedisKey, string>[2];
            redisKeyStringValuePairs[0] = new KeyValuePair<RedisKey, string>("tk1", "tv1");
            redisKeyStringValuePairs[1] = new KeyValuePair<RedisKey, string>("tk2", "tv2");

            var parameters = CombineParameters("first", simpleArray, redisKeyStringValuePairs, "second");

            Assert.Equal("first", parameters[0]);
            Assert.Equal(1, parameters[1]);
            Assert.Equal(2, parameters[2]);
            Assert.Equal("tk1", parameters[3]);
            Assert.Equal("tv1", parameters[4]);
            Assert.Equal("tk2", parameters[5]);
            Assert.Equal("tv2", parameters[6]);
            Assert.Equal("second", parameters[7]);
        }
    }
}