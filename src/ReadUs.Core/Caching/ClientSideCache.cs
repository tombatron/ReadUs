using System.Collections.Concurrent;
using System.Text;
using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Caching;

/// <summary>
/// RESP3 <c>CLIENT TRACKING</c>-backed client-side cache (project spec §10: "a natural
/// fit for the 'minimal allocation, fewer round trips' goal, worth designing in from
/// the start"). Owns one dedicated connection — tracking is a property of a single
/// connection, so the cache can't be layered transparently over the Tier 1 pool the
/// way ordinary commands are.
///
/// Supports both <see cref="TrackingMode.Default"/> (the server remembers exactly the
/// keys this connection has read) and <see cref="TrackingMode.Bcast"/> (the server
/// invalidates every write under given prefixes, whether or not this connection ever
/// read the key). Redirected tracking (<c>CLIENT TRACKING ... REDIRECT</c>) is not
/// implemented — it exists for RESP2 clients that can't receive an unsolicited
/// invalidation on an ordinary command connection at all and so need a second,
/// dedicated pub/sub connection to receive it on; this client always negotiates RESP3,
/// which delivers invalidations as an ordinary out-of-band push on the same
/// connection, so there's nothing for a ReadUs-specific redirect target to do.
/// </summary>
public sealed class ClientSideCache : IAsyncDisposable
{
    private readonly RedisConnection _connection;
    private readonly TrackingMode _mode;
    private readonly ConcurrentDictionary<string, RedisResult> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _pendingReads = new();

    // Set (under the per-key stripe lock) when an invalidation arrives for a key that is
    // still mid-read; GetAsync's post-await completion checks this, under the same lock,
    // before trusting what it's about to cache. See GetAsync's remarks.
    private readonly ConcurrentDictionary<string, byte> _invalidatedWhilePending = new();

    // A read-through's "did an invalidation land while my GET was in flight?" check and
    // an incoming invalidation's "is that key currently mid-read?" check must be a single
    // atomic step, not two independent ConcurrentDictionary calls — see GetAsync's
    // remarks for the concrete interleaving that a lock-free version of this got wrong.
    // Striping on the key keeps unrelated keys from contending with each other without
    // growing an unbounded lock table.
    private static readonly int StripeCount = Environment.ProcessorCount * 4;
    private readonly object[] _stripeLocks = CreateStripeLocks();

    private static object[] CreateStripeLocks()
    {
        var locks = new object[StripeCount];
        for (var i = 0; i < locks.Length; i++)
        {
            locks[i] = new object();
        }

        return locks;
    }

    private object StripeLockFor(string key) => _stripeLocks[(uint)key.GetHashCode() % (uint)_stripeLocks.Length];

    private void LockAllStripesAndRun(Action action) => LockAllStripesAndRun(0, action);

    private void LockAllStripesAndRun(int stripeIndex, Action action)
    {
        if (stripeIndex == _stripeLocks.Length)
        {
            action();
            return;
        }

        lock (_stripeLocks[stripeIndex])
        {
            LockAllStripesAndRun(stripeIndex + 1, action);
        }
    }

    private ClientSideCache(RedisConnection connection, TrackingMode mode)
    {
        _connection = connection;
        _mode = mode;
    }

    public static Task<ClientSideCache> ConnectAsync(RedisConnectionOptions options, CancellationToken cancellationToken = default) =>
        ConnectAsync(options, TrackingMode.Default, cancellationToken);

    public static async Task<ClientSideCache> ConnectAsync(RedisConnectionOptions options, TrackingMode mode, CancellationToken cancellationToken = default)
    {
        var connection = await RedisConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        var cache = new ClientSideCache(connection, mode);
        connection.OnPush = cache.HandleInvalidation;

        var reply = await connection.SendAsync("CLIENT"u8.ToArray(), mode.BuildClientTrackingArgs(), cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new RedisConnectionException($"CLIENT TRACKING ON failed: {reply.AsString()}");
        }

        return cache;
    }

    public int Count => _cache.Count;

    public bool TryGetCached(string key, out RedisResult value) => _cache.TryGetValue(key, out value);

    /// <summary>
    /// Reads a key, serving from the local cache when possible; a miss reads through to
    /// the server (registering the key for invalidation, since tracking is on for this
    /// connection) and populates the cache before returning.
    ///
    /// The read-through path has a real race to close: an invalidation for this exact key
    /// can arrive from the server at any point between issuing the GET and populating the
    /// cache with its result. Two earlier versions of this method got the shape of the fix
    /// right (mark "pending", write the cache, check whether an invalidation raced in) but
    /// not the atomicity: with the pending-check and the cache-write as independent
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> calls, an invalidation's own
    /// "remove from cache" and "is this key pending" checks are two separate operations
    /// too, and this method's entire write-then-clear-pending sequence can run completely
    /// *between* them — the invalidation's cache-removal finds nothing yet (the write
    /// hasn't happened), and by the time its pending-check runs, this method has already
    /// cleared the pending marker, so the invalidation is dropped with nothing left to
    /// catch it. A 50-iteration concurrent-write stress test caught this reliably, with a
    /// sequence-numbered trace of the individual dictionary operations proving the
    /// interleaving.
    ///
    /// The fix: a per-key lock (striped, so unrelated keys don't contend) that makes this
    /// method's post-await completion and <see cref="HandleInvalidation"/>'s per-key
    /// handling atomic with respect to each other. Neither ever holds the lock across a
    /// network round trip.
    ///
    /// In <see cref="TrackingMode.Bcast"/> with explicit prefixes, a key outside every
    /// registered prefix is rejected client-side before anything is sent — the same
    /// "catch it before it's a problem" shape as CROSSSLOT (Cluster) and the
    /// read-preference check (Cluster's <c>ExecuteAsync</c> overload): the server would
    /// never invalidate that key for this connection, so caching it would be a value
    /// this cache can never learn is stale.
    /// </summary>
    public async ValueTask<RedisResult> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_mode.CoversKey(key))
        {
            throw new ArgumentException(
                $"'{key}' isn't covered by any of this cache's BCAST prefixes — reading it through this cache would cache a value this connection can never learn is stale.",
                nameof(key));
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var stripeLock = StripeLockFor(key);

        lock (stripeLock)
        {
            _pendingReads[key] = 0;
        }

        RedisResult result;
        try
        {
            result = await _connection.SendAsync("GET"u8.ToArray(), [Encoding.UTF8.GetBytes(key)], cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (stripeLock)
            {
                _pendingReads.TryRemove(key, out _);
            }

            throw;
        }

        lock (stripeLock)
        {
            _cache[key] = result;

            if (_invalidatedWhilePending.TryRemove(key, out _))
            {
                // An invalidation for this exact key was processed, under this same
                // lock, while the GET above was still in flight — don't trust the value
                // we just fetched. The next GetAsync reads through again.
                _cache.TryRemove(key, out _);
            }

            _pendingReads.TryRemove(key, out _);
        }

        return result;
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    private void HandleInvalidation(RedisResult push)
    {
        var fields = push.AsItems();
        if (fields.Length < 1 || fields[0].AsString() != "invalidate")
        {
            return;
        }

        if (fields.Length < 2 || fields[1].IsNull)
        {
            // A null payload means "flush everything" — e.g. FLUSHALL, or the server's
            // invalidation table overflowed and gave up tracking individual keys.
            // GetAsync's post-await completion only ever holds one stripe at a time, so
            // holding every stripe simultaneously here makes this whole flush atomic
            // with respect to every in-flight read across every key, the same way a
            // single stripe makes one key's invalidation atomic with that key's read.
            LockAllStripesAndRun(() =>
            {
                _cache.Clear();
                foreach (var pendingKey in _pendingReads.Keys)
                {
                    _invalidatedWhilePending[pendingKey] = 0;
                }
            });

            return;
        }

        foreach (var keyResult in fields[1].AsItems())
        {
            var key = keyResult.AsString();
            lock (StripeLockFor(key))
            {
                _cache.TryRemove(key, out _);
                if (_pendingReads.ContainsKey(key))
                {
                    _invalidatedWhilePending[key] = 0;
                }
            }
        }
    }
}
