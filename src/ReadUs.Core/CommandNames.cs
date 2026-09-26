namespace ReadUs;

/// <summary>
/// Hand-written command-name byte arrays for the connection-layer commands issued
/// below <see cref="RedisClient"/> — the handshake's <c>CLIENT ID</c>
/// (<see cref="Connections.RedisConnection"/>) and the blocking-cancellation
/// reconciliation's <c>CLIENT UNBLOCK</c> (also <see cref="Connections.RedisConnection"/>),
/// plus the transaction builder's <c>WATCH</c>/<c>MULTI</c>/<c>EXEC</c>/<c>DISCARD</c>
/// (<see cref="Transactions.RedisTransaction"/>), which runs over a
/// <see cref="Pooling.ConnectionLease"/> rather than a <see cref="RedisClient"/>.
///
/// The command-table source generator (project spec §3, §13 step 4) now covers
/// everything reachable through <see cref="RedisClient"/> itself — see
/// <c>ReadUs.Generated</c> — so this class only needs to remain for the layers below
/// and beside it that the generator doesn't target (a documented gap: extending
/// generated methods to work over a leased connection/transaction context, not just
/// <see cref="RedisClient"/>, is future work — see docs/design/state-machines.md §5).
/// </summary>
internal static class CommandNames
{
    public static readonly byte[] Client = "CLIENT"u8.ToArray();
    public static readonly byte[] ClientIdSubcommand = "ID"u8.ToArray();
    public static readonly byte[] ClientUnblockSubcommand = "UNBLOCK"u8.ToArray();
    public static readonly byte[] UnblockTimeoutMode = "TIMEOUT"u8.ToArray();
    public static readonly byte[] Watch = "WATCH"u8.ToArray();
    public static readonly byte[] Multi = "MULTI"u8.ToArray();
    public static readonly byte[] Exec = "EXEC"u8.ToArray();
    public static readonly byte[] Discard = "DISCARD"u8.ToArray();
}
