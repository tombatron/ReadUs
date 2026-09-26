using ReadUs.Pooling;
using ReadUs.Protocol;
// CommandNames lives in the root ReadUs namespace (see CommandNames.cs).
using ReadUs;

namespace ReadUs.Transactions;

/// <summary>
/// A <c>MULTI</c>/<c>WATCH</c>/<c>EXEC</c> transaction (project spec §10). Owns a Tier 2
/// leased connection for its entire lifetime — <c>WATCH</c> and <c>EXEC</c> are only
/// meaningful when issued on the same physical connection (project spec §4) — and
/// returns it deterministically on disposal.
///
/// Usage: <c>await using var tx = await RedisTransaction.StartAsync(pool); await
/// tx.WatchAsync(...); await tx.MultiAsync(); await tx.QueueAsync(...); var result =
/// await tx.ExecAsync();</c>. If disposed without a call to <see cref="ExecAsync"/> or
/// <see cref="DiscardAsync"/>, the transaction is discarded defensively so the
/// connection is never returned to the pool sitting mid-<c>MULTI</c>.
/// </summary>
public sealed class RedisTransaction : IAsyncDisposable
{
    private readonly ConnectionLease _lease;
    private bool _multiSent;
    private bool _finished;

    private RedisTransaction(ConnectionLease lease) => _lease = lease;

    public static async Task<RedisTransaction> StartAsync(LeasedConnectionPool pool, CancellationToken cancellationToken = default)
    {
        var lease = await pool.LeaseAsync(cancellationToken).ConfigureAwait(false);
        return new RedisTransaction(lease);
    }

    /// <summary>Must be called before <see cref="MultiAsync"/> — WATCH after MULTI is a protocol error on the server.</summary>
    public async ValueTask WatchAsync(ReadOnlyMemory<byte>[] keys, CancellationToken cancellationToken = default)
    {
        if (_multiSent)
        {
            throw new InvalidOperationException("WatchAsync must be called before MultiAsync.");
        }

        var result = await _lease.ExecuteAsync(CommandNames.Watch, keys, cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
    }

    public async ValueTask MultiAsync(CancellationToken cancellationToken = default)
    {
        if (_multiSent)
        {
            throw new InvalidOperationException("MultiAsync has already been called.");
        }

        var result = await _lease.ExecuteAsync(CommandNames.Multi, [], cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
        _multiSent = true;
    }

    /// <summary>
    /// Queues a command inside the open <c>MULTI</c>. The server's per-command reply
    /// here is just <c>QUEUED</c> (or an error if the command was malformed) — the
    /// command's real result only comes back from <see cref="ExecAsync"/>.
    /// </summary>
    public async ValueTask QueueAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default)
    {
        if (!_multiSent)
        {
            throw new InvalidOperationException("Call MultiAsync before queuing commands.");
        }

        var result = await _lease.ExecuteAsync(commandName, args, cancellationToken).ConfigureAwait(false);
        ThrowIfError(result);
    }

    /// <summary>
    /// Executes the transaction. The result is an array of each queued command's reply,
    /// in order — or <see cref="RedisResult.IsNull"/> if a watched key changed and the
    /// server aborted the transaction.
    /// </summary>
    public async ValueTask<RedisResult> ExecAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotFinished();
        var result = await _lease.ExecuteAsync(CommandNames.Exec, [], cancellationToken).ConfigureAwait(false);
        _finished = true;
        return result;
    }

    public async ValueTask DiscardAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotFinished();
        await _lease.ExecuteAsync(CommandNames.Discard, [], cancellationToken).ConfigureAwait(false);
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished && _multiSent)
        {
            try
            {
                await _lease.ExecuteAsync(CommandNames.Discard, [], CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The connection may already be dead; ConnectionLease.DisposeAsync below
                // checks its state and won't re-pool it either way (Invariant I5).
            }
        }

        await _lease.DisposeAsync().ConfigureAwait(false);
    }

    private static void ThrowIfError(RedisResult result)
    {
        if (result.IsError)
        {
            throw new RedisTransactionException(result.AsString());
        }
    }

    private void EnsureNotFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The transaction has already been completed.");
        }
    }
}
