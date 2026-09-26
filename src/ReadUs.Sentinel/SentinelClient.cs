using System.Globalization;
using System.Net;
using System.Text;
using ReadUs.Connections;
using ReadUs.Protocol;
using ReadUs.Transactions;

namespace ReadUs.Sentinel;

/// <summary>
/// Sentinel-backed master discovery and failover (project spec §6). Sentinel is purely
/// a discovery/failover layer on top of the standard connection stack — once the
/// current master is known, commands run through an ordinary <see cref="RedisClient"/>
/// (full Tier 1/Tier 2 pools) exactly like the standalone client, per the spec's own
/// framing. This type just knows how to find that master and swap it out on failover.
///
/// Failover detection is two-layered, per spec: a dedicated pub/sub connection to a
/// sentinel's <c>+switch-master</c> channel is the primary, fast signal
/// (<see cref="SentinelPubSubMonitor"/>), with periodic polling
/// (<c>SENTINEL get-master-addr-by-name</c>) as a safety net in case that connection
/// itself silently drops. Master discovery never trusts a single sentinel's answer — it
/// queries every known sentinel and requires a strict majority of *responders* to agree
/// (project spec §6's explicit call-out: "don't just trust the first responder").
/// </summary>
public sealed class SentinelClient : IAsyncDisposable
{
    private const int MaxDiscoveryAttempts = 5;
    private static readonly TimeSpan DiscoveryRetryBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PerSentinelQueryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PubSubReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly string _serviceName;
    private readonly Func<EndPoint, RedisConnectionOptions> _masterOptionsFactory;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _failoverGate = new(1, 1);
    private readonly Lock _sentinelEndpointsLock = new();
    private readonly List<EndPoint> _sentinelEndpoints;

    private volatile RedisClient _masterClient = null!;
    private volatile string _masterEndpointKey = string.Empty;

    private Task _pollLoopTask = Task.CompletedTask;
    private Task _pubSubSupervisorTask = Task.CompletedTask;

    private SentinelClient(string serviceName, List<EndPoint> sentinelEndpoints, Func<EndPoint, RedisConnectionOptions> masterOptionsFactory)
    {
        _serviceName = serviceName;
        _sentinelEndpoints = sentinelEndpoints;
        _masterOptionsFactory = masterOptionsFactory;
    }

    public static async Task<SentinelClient> ConnectAsync(
        IReadOnlyList<EndPoint> sentinelEndpoints,
        string serviceName,
        Func<EndPoint, RedisConnectionOptions>? masterOptionsFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (sentinelEndpoints.Count == 0)
        {
            throw new ArgumentException("At least one sentinel endpoint is required.", nameof(sentinelEndpoints));
        }

        masterOptionsFactory ??= static endpoint => new RedisConnectionOptions { EndPoint = endpoint };

        var client = new SentinelClient(serviceName, [.. sentinelEndpoints], masterOptionsFactory);
        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    /// <summary>The raw escape hatch (project spec §3), routed to the currently-known master.</summary>
    public ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        _masterClient.ExecuteAsync(commandName, args, cancellationToken);

    public ValueTask<RedisResult> ExecuteBlockingAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        _masterClient.ExecuteBlockingAsync(commandName, args, cancellationToken);

    public Task<RedisTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        _masterClient.BeginTransactionAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pollLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await _pubSubSupervisorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _masterClient.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
        _failoverGate.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var masterEndpoint = await DiscoverMasterAsync(cancellationToken).ConfigureAwait(false);
        _masterClient = await RedisClient.ConnectAsync(_masterOptionsFactory(masterEndpoint), cancellationToken: cancellationToken).ConfigureAwait(false);
        _masterEndpointKey = masterEndpoint.ToString()!;

        _pollLoopTask = Task.Run(PollLoopAsync, CancellationToken.None);
        _pubSubSupervisorTask = Task.Run(PubSubSupervisorLoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// Queries every known sentinel concurrently and requires a strict majority of
    /// whoever actually responded to agree on the same address before accepting it —
    /// never the first response alone (project spec §6). Retries with backoff if no
    /// majority forms (e.g. mid-failover, sentinels briefly disagreeing).
    /// </summary>
    private async Task<EndPoint> DiscoverMasterAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaxDiscoveryAttempts; attempt++)
        {
            var endpoints = GetSentinelEndpointsSnapshot();

            var queries = endpoints.Select(async endpoint =>
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(PerSentinelQueryTimeout);

                    var reply = await QuerySentinelAsync(endpoint, "get-master-addr-by-name"u8.ToArray(), [Encoding.UTF8.GetBytes(_serviceName)], timeoutCts.Token).ConfigureAwait(false);
                    if (reply.IsNull)
                    {
                        return null;
                    }

                    var parts = reply.AsItems();
                    return (EndPoint)new DnsEndPoint(parts[0].AsString(), (int)parts[1].AsInt64());
                }
                catch
                {
                    return null;
                }
            });

            var results = await Task.WhenAll(queries).ConfigureAwait(false);
            var responses = results.Where(r => r is not null).Select(r => r!).ToList();

            if (responses.Count > 0)
            {
                var winner = responses
                    .GroupBy(ep => ep.ToString())
                    .OrderByDescending(g => g.Count())
                    .First();

                if (winner.Count() * 2 > responses.Count)
                {
                    return responses.First(ep => ep.ToString() == winner.Key);
                }
            }

            await Task.Delay(DiscoveryRetryBackoff, cancellationToken).ConfigureAwait(false);
        }

        throw new SentinelDiscoveryException($"Could not reach quorum discovering the master for service '{_serviceName}' after {MaxDiscoveryAttempts} attempts.");
    }

    /// <summary>Grows the known sentinel set with any newly-discovered peers (project spec §6) — never prunes ones that stop responding, a documented v0 simplification (see docs/design/state-machines.md §5).</summary>
    private async Task RefreshSentinelListAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in GetSentinelEndpointsSnapshot())
        {
            try
            {
                var reply = await QuerySentinelAsync(endpoint, "sentinels"u8.ToArray(), [Encoding.UTF8.GetBytes(_serviceName)], cancellationToken).ConfigureAwait(false);
                AddSentinelEndpoints(ParsePeerEndpoints(reply));
                return;
            }
            catch
            {
                // Try the next known sentinel.
            }
        }
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, _lifetimeCts.Token).ConfigureAwait(false);

                try
                {
                    await RefreshSentinelListAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    var discovered = await DiscoverMasterAsync(_lifetimeCts.Token).ConfigureAwait(false);
                    await SwitchMasterAsync(discovered, _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch when (!_lifetimeCts.IsCancellationRequested)
                {
                    // The current master is kept; the next tick tries again. This is
                    // the safety net, not the primary signal — a transient failure here
                    // isn't itself an emergency.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PubSubSupervisorLoopAsync()
    {
        while (!_lifetimeCts.IsCancellationRequested)
        {
            bool connected = false;

            foreach (var endpoint in GetSentinelEndpointsSnapshot())
            {
                try
                {
                    await using var monitor = await SentinelPubSubMonitor.SubscribeAsync(endpoint, OnSwitchMasterMessage, _lifetimeCts.Token).ConfigureAwait(false);
                    connected = true;

                    // `monitor.Completion` only resolves when its connection drops on
                    // its own — it does not observe `_lifetimeCts` at all, since the
                    // monitor has no idea this client even exists. Racing it against
                    // our own cancellation (rather than awaiting it directly) is what
                    // lets `DisposeAsync` actually make progress: the `await using`
                    // above disposes `monitor` — which cancels its internal token and
                    // is what makes `Completion` resolve in the first place — only
                    // once this line returns. Awaiting `Completion` directly would be
                    // a circular wait: shutdown would depend on a message that will
                    // never arrive to unblock the very code that would cancel it.
                    var lifetimeCancelled = Task.Delay(Timeout.Infinite, _lifetimeCts.Token);
                    await Task.WhenAny(monitor.Completion, lifetimeCancelled).ConfigureAwait(false);

                    if (_lifetimeCts.IsCancellationRequested)
                    {
                        return;
                    }

                    break;
                }
                catch when (!_lifetimeCts.IsCancellationRequested)
                {
                    // Try the next sentinel.
                }
            }

            if (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(connected ? TimeSpan.Zero : PubSubReconnectDelay, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnSwitchMasterMessage(string payload)
    {
        // "<master name> <old ip> <old port> <new ip> <new port>"
        var parts = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !string.Equals(parts[0], _serviceName, StringComparison.Ordinal))
        {
            return;
        }

        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int newPort))
        {
            return;
        }

        _ = SwitchMasterAsync(new DnsEndPoint(parts[3], newPort), _lifetimeCts.Token);
    }

    private async Task SwitchMasterAsync(EndPoint newEndpoint, CancellationToken cancellationToken)
    {
        string newKey = newEndpoint.ToString()!;
        if (newKey == _masterEndpointKey)
        {
            return;
        }

        await _failoverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (newKey == _masterEndpointKey)
            {
                return;
            }

            var newClient = await RedisClient.ConnectAsync(_masterOptionsFactory(newEndpoint), cancellationToken: cancellationToken).ConfigureAwait(false);
            var oldClient = Interlocked.Exchange(ref _masterClient, newClient);
            _masterEndpointKey = newKey;

            await oldClient.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _failoverGate.Release();
        }
    }

    private List<EndPoint> GetSentinelEndpointsSnapshot()
    {
        lock (_sentinelEndpointsLock)
        {
            return [.. _sentinelEndpoints];
        }
    }

    private void AddSentinelEndpoints(IEnumerable<EndPoint> discovered)
    {
        lock (_sentinelEndpointsLock)
        {
            foreach (var endpoint in discovered)
            {
                if (!_sentinelEndpoints.Exists(e => e.ToString() == endpoint.ToString()))
                {
                    _sentinelEndpoints.Add(endpoint);
                }
            }
        }
    }

    private static List<EndPoint> ParsePeerEndpoints(RedisResult sentinelsReply)
    {
        var endpoints = new List<EndPoint>();

        foreach (var entry in sentinelsReply.AsItems())
        {
            string? ip = null;
            long port = 0;
            var fields = entry.AsItems();

            for (int i = 0; i + 1 < fields.Length; i += 2)
            {
                string key = fields[i].AsString();
                if (key == "ip")
                {
                    ip = fields[i + 1].AsString();
                }
                else if (key == "port")
                {
                    port = fields[i + 1].AsInt64();
                }
            }

            if (ip is not null)
            {
                endpoints.Add(new DnsEndPoint(ip, (int)port));
            }
        }

        return endpoints;
    }

    private static async Task<RedisResult> QuerySentinelAsync(EndPoint endpoint, ReadOnlyMemory<byte> subcommand, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken)
    {
        var options = new RedisConnectionOptions { EndPoint = endpoint, ConnectTimeout = PerSentinelQueryTimeout };
        await using var connection = await RedisConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

        var fullArgs = new ReadOnlyMemory<byte>[args.Length + 1];
        fullArgs[0] = subcommand;
        args.CopyTo(fullArgs, 1);

        return await connection.SendAsync("SENTINEL"u8.ToArray(), fullArgs, cancellationToken).ConfigureAwait(false);
    }
}
