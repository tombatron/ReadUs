namespace ReadUs.Connections;

/// <summary>
/// Mirrors the connection lifecycle FSM in docs/design/state-machines.md §1. Stored as
/// the backing type of an <c>int</c> field so transitions that matter for correctness
/// (into <see cref="Faulted"/>, specifically) can go through <see cref="Interlocked"/>
/// and satisfy Invariant I1 (single owner per transition).
/// </summary>
internal enum ConnectionState
{
    Created,
    Connecting,
    // Unused until TLS lands (project spec §7, managed-Redis phase): Connecting goes
    // straight to ProtocolHandshake today. Kept in the enum so it isn't inserted later
    // and renumber everything after it.
    TlsHandshaking,
    ProtocolHandshake,
    Authenticating,
    Ready,
    Draining,
    Faulted,
    Closed,
}
