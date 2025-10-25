namespace ReadUs.Extras;

public static class KeyValuePairExtensions // TODO: Move this to the extras namespace.
{
    private static readonly RedisKey[] EmptyRedisKeyArray = [];

    public static RedisKey[]? Keys(this KeyValuePair<RedisKey, string>[]? @this)
    {
        if (@this is null)
        {
            return null;
        }

        return @this.Length != 0 ? @this.Select(x => x.Key).ToArray() : EmptyRedisKeyArray;
    }
}