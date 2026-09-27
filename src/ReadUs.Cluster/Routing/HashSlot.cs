namespace ReadUs.Cluster.Routing;

/// <summary>
/// Redis Cluster's key→slot mapping (project spec §5): <c>CRC16(hashtag) % 16384</c>,
/// where the hash tag is the substring between the first <c>{</c> and the next
/// <c>}</c> after it — or the whole key when there's no such substring (missing,
/// empty, or unmatched braces all fall back to hashing the whole key).
/// </summary>
public static class HashSlot
{
    public const int Count = 16384;

    public static int Compute(ReadOnlySpan<byte> key) => Crc16.Compute(ExtractHashTag(key)) % Count;

    /// <summary>
    /// Exposed separately from <see cref="Compute"/> because the hash-tag rule is
    /// exactly the kind of off-by-one-prone logic the project spec calls out by name
    /// ("a classic client bug") — worth being independently testable.
    /// </summary>
    public static ReadOnlySpan<byte> ExtractHashTag(ReadOnlySpan<byte> key)
    {
        var openBrace = key.IndexOf((byte)'{');
        if (openBrace < 0)
        {
            return key;
        }

        var afterOpenBrace = key[(openBrace + 1)..];
        var closeBrace = afterOpenBrace.IndexOf((byte)'}');

        // closeBrace == 0 means "{}" — an empty tag — which also falls back to the
        // whole key, same as no closing brace being found at all (closeBrace < 0).
        return closeBrace <= 0 ? key : afterOpenBrace[..closeBrace];
    }
}
