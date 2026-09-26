namespace ReadUs.Cluster;

/// <summary>
/// Thrown client-side, before a command is ever sent, when its key arguments compute
/// to different slots (project spec §5) — a client bug the spec says to catch
/// pre-flight rather than rely on the server's <c>CROSSSLOT</c> error.
/// </summary>
public sealed class ClusterCrossSlotException : Exception
{
    public ClusterCrossSlotException(string message) : base(message)
    {
    }
}

/// <summary>Surfaced distinctly from ordinary connection failures (project spec §5) so callers can apply a longer backoff.</summary>
public sealed class ClusterDownException : Exception
{
    public ClusterDownException(string message) : base(message)
    {
    }
}

/// <summary>The redirect cap (MOVED/ASK/TRYAGAIN) was hit without the command completing — the slot map isn't stabilizing (project spec §5).</summary>
public sealed class ClusterTooManyRedirectsException : Exception
{
    public ClusterTooManyRedirectsException(string message) : base(message)
    {
    }
}
