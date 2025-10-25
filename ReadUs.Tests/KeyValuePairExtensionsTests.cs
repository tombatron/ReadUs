using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using ReadUs.Extras;
using Xunit;

namespace ReadUs.Tests;

[UsedImplicitly]
public class KeyValuePairExtensionsTests
{
    [Fact]
    public void IfInputIsNullOutputIsNull()
    {
        var testResult = default(KeyValuePair<RedisKey, string>[]).Keys();

        Assert.Null(testResult);
    }

    [Fact]
    public void IfInputIsEmptyOutputIsEmpty()
    {
        var emptyArray = Array.Empty<KeyValuePair<RedisKey, string>>();

        var testResult = emptyArray.Keys();

        Assert.NotNull(testResult);
        Assert.Empty(testResult);
    }

    [Fact]
    public void ItWillReturnKeysFromAnArrayOfKeyValuePairs()
    {
        var testArray = new KeyValuePair<RedisKey, string>[2];

        testArray[0] = new KeyValuePair<RedisKey, string>("key1", "value1");
        testArray[1] = new KeyValuePair<RedisKey, string>("key2", "value2");

        var testResult = testArray.Keys();

        Assert.NotNull(testResult);
        Assert.Equal("key1", testResult[0]);
        Assert.Equal("key2", testResult[1]);
    }
}