namespace ReadUs.Cluster;

/// <summary>
/// Thrown client-side, before a command is ever sent, when its key arguments compute
/// to different slots (project spec §5) — a client bug the spec says to catch
/// pre-flight rather than rely on the server's <c>CROSSSLOT</c> error.
/// </summary>
public sealed class ClusterCrossSlotException(string message) : Exception(message)
{
}

/// <summary>Surfaced distinctly from ordinary connection failures (project spec §5) so callers can apply a longer backoff.</summary>
public sealed class ClusterDownException(string message) : Exception(message)
{
}

/// <summary>The redirect cap (MOVED/ASK/TRYAGAIN) was hit without the command completing — the slot map isn't stabilizing (project spec §5).</summary>
public sealed class ClusterTooManyRedirectsException(string message) : Exception(message)
{
}

/// <summary>
/// Thrown instead of even attempting a call, when the node it would route to has
/// failed enough consecutive times to be quarantined (design doc §3.1) — a
/// fail-fast so a repeatedly-unreachable node doesn't cost every caller a full
/// connection-attempt's worth of latency. A background prober reintegrates the node
/// on its own schedule; this exception says nothing about when that will happen.
/// </summary>
public sealed class ClusterNodeQuarantinedException(string message) : Exception(message)
{
}
