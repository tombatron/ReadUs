using System.Text;

namespace ReadUs.Caching;

/// <summary>
/// How <c>CLIENT TRACKING</c> is configured for a <see cref="ClientSideCache"/>
/// connection (RESP3, project spec §10).
/// </summary>
public readonly struct TrackingMode
{
    private readonly string[]? _bcastPrefixes;

    private TrackingMode(string[]? bcastPrefixes) => _bcastPrefixes = bcastPrefixes;

    /// <summary>
    /// The server remembers exactly the keys this connection has read and invalidates
    /// only those. Best for a cache with a small, hot working set — the server's
    /// per-connection tracking table grows with how many distinct keys get read, and
    /// can overflow (falling back to a blanket "flush everything" invalidation) if
    /// that set gets too large.
    /// </summary>
    public static TrackingMode Default => default;

    /// <summary>
    /// The server invalidates every write under the given prefixes, regardless of
    /// whether this connection ever actually read the key — no per-key tracking table
    /// on the server, so it scales to an arbitrarily large key space at the cost of
    /// some invalidations for keys this cache never touched. No prefixes means every
    /// key in the keyspace.
    /// </summary>
    public static TrackingMode Bcast(params string[] prefixes) => new(prefixes);

    internal bool IsBcast => _bcastPrefixes is not null;

    internal IReadOnlyList<string> BcastPrefixes => _bcastPrefixes ?? [];

    /// <summary>True for <see cref="Default"/>, or for <see cref="Bcast"/> with no prefixes (every key is in scope); false only for BCAST with one or more explicit prefixes that <paramref name="key"/> doesn't start with.</summary>
    internal bool CoversKey(string key) =>
        _bcastPrefixes is not { Length: > 0 } prefixes || Array.Exists(prefixes, prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    internal ReadOnlyMemory<byte>[] BuildClientTrackingArgs()
    {
        if (_bcastPrefixes is null)
        {
            return ["TRACKING"u8.ToArray(), "ON"u8.ToArray()];
        }

        var args = new List<ReadOnlyMemory<byte>> { "TRACKING"u8.ToArray(), "ON"u8.ToArray(), "BCAST"u8.ToArray() };
        foreach (var prefix in _bcastPrefixes)
        {
            args.Add("PREFIX"u8.ToArray());
            args.Add(Encoding.UTF8.GetBytes(prefix));
        }

        return [.. args];
    }
}
