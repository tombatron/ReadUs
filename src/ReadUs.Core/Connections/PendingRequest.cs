using System.Threading.Tasks.Sources;
using ReadUs.Protocol;

namespace ReadUs.Connections;

/// <summary>
/// One in-flight request's completion slot. Pooled (see <see cref="RedisConnection"/>)
/// to keep steady-state command execution free of a per-call <c>Task&lt;T&gt;</c>
/// allocation (project spec §8).
///
/// The completion-claim CAS here is the same primitive the blocking-cancellation race
/// spec (docs/design/state-machines.md §2.4) requires for Tier 2 leased connections,
/// applied at Tier 1 scope: the read loop (a real reply arrived) and a caller's
/// <see cref="CancellationToken"/> firing race to complete this request, and exactly
/// one of them may win. The loser's write is a no-op. Unlike the Tier 2 blocking case,
/// a Tier 1 cancellation never removes the request from the connection's pending
/// queue — the wire reply is still coming and must still be consumed to keep the
/// connection's FIFO reply order intact — so a cancelled-then-answered request simply
/// discards the eventual real reply (see <see cref="TryCompleteWithResult"/> returning
/// <see langword="false"/> in <c>RedisConnection.DispatchReply</c>).
/// </summary>
internal sealed class PendingRequest : IValueTaskSource<RedisResult>
{
    private ManualResetValueTaskSourceCore<RedisResult> _core;
    private int _claimed;

    public PendingRequest() => _core.RunContinuationsAsynchronously = true;

    public short Version => _core.Version;

    public bool TryCompleteWithResult(RedisResult result)
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            return false;
        }

        _core.SetResult(result);
        return true;
    }

    public bool TryCompleteWithException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            return false;
        }

        _core.SetException(exception);
        return true;
    }

    /// <summary>
    /// Resets this instance for reuse. Must only be called after the single consumer
    /// that awaited this request's <see cref="ValueTask{TResult}"/> has observed its
    /// result — i.e. from the same call site that constructed that ValueTask, after
    /// awaiting it, never speculatively. Calling this earlier would violate the
    /// pooled-<see cref="IValueTaskSource{TResult}"/> single-consumption contract
    /// (project spec §9.5).
    /// </summary>
    public void ResetForPooling()
    {
        _core.Reset();
        Volatile.Write(ref _claimed, 0);
    }

    RedisResult IValueTaskSource<RedisResult>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<RedisResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<RedisResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
