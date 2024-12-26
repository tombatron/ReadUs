using System.Collections.Generic;
using System.Linq;

namespace ReadUs;

internal static class KeyValuePairExtensions
{
    private static readonly RedisKey[] EmptyRedisKeyArray = [];

    internal static RedisKey[]? Keys(this KeyValuePair<RedisKey, string>[]? @this)
    {
        if (@this is null) return default;

        return @this.Length != 0 ? @this.Select(x => x.Key).ToArray() : EmptyRedisKeyArray;
    }
}