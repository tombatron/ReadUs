using System.Diagnostics.Metrics;

namespace ReadUs.Diagnostics;

/// <summary>
/// Metrics instrumentation (project spec §10: "OpenTelemetry metrics/tracing... as
/// separate, optional packages that add zero overhead when not referenced"). Built
/// entirely on the BCL's <see cref="System.Diagnostics.Metrics"/> — not the
/// OpenTelemetry SDK — so <c>ReadUs.Core</c> takes no OpenTelemetry package dependency
/// at all. <c>ReadUs.Extensions.OpenTelemetry</c> is a thin, separate package that just
/// points a real <c>MeterProvider</c> at <see cref="MeterName"/>; with no listener
/// attached (the OpenTelemetry SDK not referenced, or referenced but not configured to
/// listen to this meter), instrument calls are cheap no-ops per the BCL's own design.
///
/// Deferred decision (see docs/design/state-machines.md §5): tags are kept coarse
/// (tier, redirect kind) rather than per-command-name, since decoding a command name
/// to a string for every call would be an allocation on the hot path the rest of the
/// project spends real effort avoiding (§8) — a cost that shouldn't exist just because
/// a meter happens to be listening.
/// </summary>
public static class ReadUsDiagnostics
{
    public const string MeterName = "ReadUs";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> CommandsExecutedCounter =
        Meter.CreateCounter<long>("readus.commands.executed", unit: "{command}", description: "Number of commands sent, by tier.");

    private static readonly Histogram<double> CommandDurationHistogram =
        Meter.CreateHistogram<double>("readus.command.duration", unit: "ms", description: "Command round-trip latency, by tier.");

    private static readonly Counter<long> ConnectionsOpenedCounter =
        Meter.CreateCounter<long>("readus.connections.opened", unit: "{connection}", description: "Physical connections successfully established.");

    private static readonly Counter<long> ConnectionsFaultedCounter =
        Meter.CreateCounter<long>("readus.connections.faulted", unit: "{connection}", description: "Physical connections that transitioned to Faulted, whether or not they ever became Ready.");

    /// <summary>
    /// Process-wide currently-Ready connection count (project spec §10's "connection
    /// pool occupancy") — an <see cref="UpDownCounter{T}"/> incremented/decremented
    /// imperatively from <see cref="ConnectionOpened"/>/<see cref="ConnectionFaulted"/>
    /// rather than an <see cref="ObservableGauge{T}"/> polled per-pool-instance.
    /// Deferred decision (see design doc §5): this is deliberately scoped process-wide,
    /// not broken out per pool/client instance — an <c>ObservableGauge</c> would need a
    /// per-instance callback registered against the single process-lifetime
    /// <see cref="Meter"/>, and individual instrument disposal to unregister it when a
    /// pool goes away isn't available on this instrument type, which would leak every
    /// pool that ever registered one for the life of the process.
    /// </summary>
    private static readonly UpDownCounter<int> ActiveConnectionsCounter =
        Meter.CreateUpDownCounter<int>("readus.connections.active", unit: "{connection}", description: "Process-wide count of connections currently in the Ready state.");

    private static readonly Counter<long> ReconnectsCounter =
        Meter.CreateCounter<long>("readus.pool.reconnects", unit: "{reconnect}", description: "Tier 1 pool slots successfully healed after a fault.");

    private static readonly Counter<long> ClusterRedirectsCounter =
        Meter.CreateCounter<long>("readus.cluster.redirects", unit: "{redirect}", description: "Cluster MOVED/ASK/TRYAGAIN redirects followed, by kind.");

    private static readonly Counter<long> WriteFlushesCounter =
        Meter.CreateCounter<long>("readus.connection.flushes", unit: "{flush}", description: "PipeWriter.FlushAsync calls made by a connection's write loop.");

    /// <summary>
    /// How many commands a single <see cref="Connections.RedisConnection"/> write-loop
    /// flush covered (design doc "Recorded during implementation of batched connection
    /// writes") — the direct, permanent version of the throwaway counters originally
    /// used to diagnose and validate that change, kept because seeing actual batch
    /// sizes for a real workload is useful beyond that one investigation.
    /// </summary>
    private static readonly Histogram<long> WriteFlushBatchSizeHistogram =
        Meter.CreateHistogram<long>("readus.connection.flush.batchsize", unit: "{command}", description: "Number of commands written into a single flush by a connection's write loop.");

    public static void CommandExecuted(string tier, double elapsedMilliseconds)
    {
        CommandsExecutedCounter.Add(1, new KeyValuePair<string, object?>("tier", tier));
        CommandDurationHistogram.Record(elapsedMilliseconds, new KeyValuePair<string, object?>("tier", tier));
    }

    public static void ConnectionOpened()
    {
        ConnectionsOpenedCounter.Add(1);
        ActiveConnectionsCounter.Add(1);
    }

    /// <param name="wasReady">Whether this connection had ever reached Ready before faulting — a connect/handshake failure never incremented <see cref="ActiveConnectionsCounter"/>, so it must not decrement it either.</param>
    public static void ConnectionFaulted(bool wasReady)
    {
        ConnectionsFaultedCounter.Add(1);
        if (wasReady)
        {
            ActiveConnectionsCounter.Add(-1);
        }
    }

    public static void Reconnected() => ReconnectsCounter.Add(1);

    public static void ClusterRedirect(string kind) => ClusterRedirectsCounter.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void WriteBatchFlushed(int batchSize)
    {
        WriteFlushesCounter.Add(1);
        WriteFlushBatchSizeHistogram.Record(batchSize);
    }
}
