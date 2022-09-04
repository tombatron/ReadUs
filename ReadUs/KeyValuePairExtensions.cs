using System.Collections.Generic;
using System.Linq;

namespace ReadUs
{
    internal static class KeyValuePairExtensions
    {
        private static readonly RedisKey[] EmptyRedisKeyArray = new RedisKey[0];

        internal static RedisKey[] Keys(this KeyValuePair<RedisKey, string>[] @this)
        {
            if (@this is null)
            {
                return default;
            }

            if (@this.Any())
            {
                return @this.Select(x => x.Key).ToArray();
            }
            else
            {
                return EmptyRedisKeyArray;
            }
        }
    }
}