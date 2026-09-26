using System.Threading.Tasks.Sources;
using ReadUs.Protocol;

namespace ReadUs.Connections;

/// <summary>
/// One in-flight request's completion slot. Pooled (see <see cref="RedisConnection"/>)
/// to keep steady-state command execution free of a per-call <c>Task&lt;T&gt;</c>
/// allocation (project spec §8).
///
/// The completion-claim CAS here is the primitive the blocking-cancellation race spec
/// (docs/design/state-machines.md §2.4) describes, used two ways depending on tier:
///
/// <list type="bullet">
/// <item>Tier 1 (<see cref="RedisConnection.SendAsync"/>): the read loop (a real reply
/// arrived) and a caller's <see cref="CancellationToken"/> firing race to complete this
/// request via <see cref="TryCompleteWithResult"/>/<see cref="TryCompleteWithException"/>
/// directly — whoever wins the claim decides the outcome immediately. A cancelled-then-
/// answered request simply discards the eventual real reply (that call returns
/// <see langword="false"/> in <c>RedisConnection.DispatchReply</c>); the request is
/// never removed from the connection's pending queue, which would desync RESP framing
/// for everything queued behind it.</item>
/// <item>Tier 2 (<see cref="RedisConnection.SendBlockingAsync"/>): cancellation claims
/// the request via <see cref="TryClaimForReconciliation"/> but does <em>not</em> decide
/// the outcome yet — a <c>CLIENT UNBLOCK</c> round trip (docs/design/state-machines.md
/// §2.3) has to happen first, and whatever frame the read loop delivers next on this
/// connection has to be inspected (it might be the unblock artifact, or it might be the
/// genuine reply that was already in flight — see §2.4's "a value can legitimately win
/// the race" policy). <see cref="TryDeliverReconciliationFrame"/> is how the read loop
/// hands that frame over instead of silently dropping it, and
/// <see cref="CompleteClaimed"/>/<see cref="CompleteClaimedWithException"/> is how the
/// reconciliation flow finally resolves the caller's <see cref="ValueTask{TResult}"/>
/// once it has decided.</item>
/// </list>
/// </summary>
internal sealed class PendingRequest : IValueTaskSource<RedisResult>
{
    private ManualResetValueTaskSourceCore<RedisResult> _core;
    private int _claimed;
    private TaskCompletionSource<RedisResult>? _reconciliationSink;

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
    /// Tier 2 only. Claims this request without deciding its outcome, and hands back a
    /// <see cref="Task{TResult}"/> that completes with whatever frame the read loop
    /// next delivers on this connection (see <see cref="TryDeliverReconciliationFrame"/>).
    /// The sink is published <em>before</em> the claim CAS so that a concurrent
    /// <see cref="TryDeliverReconciliationFrame"/> call — which only runs after
    /// observing the claim — is guaranteed to see it (no lost-wakeup window between
    /// "claimed" and "sink visible").
    /// </summary>
    public bool TryClaimForReconciliation(out Task<RedisResult> frameDelivered)
    {
        var sink = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _reconciliationSink, sink);

        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
        {
            frameDelivered = Task.FromCanceled<RedisResult>(new CancellationToken(true));
            return false;
        }

        frameDelivered = sink.Task;
        return true;
    }

    /// <summary>
    /// Called from <c>RedisConnection.DispatchReply</c> when the head-of-queue request
    /// is already claimed (so <see cref="TryCompleteWithResult"/> returned
    /// <see langword="false"/>). Hands the frame to an in-progress reconciliation if
    /// one is waiting; otherwise (Tier 1 cancellation, which never claims a
    /// reconciliation sink) this is a no-op and the frame is discarded, matching Tier
    /// 1's documented policy.
    /// </summary>
    public bool TryDeliverReconciliationFrame(RedisResult result)
    {
        var sink = Volatile.Read(ref _reconciliationSink);
        return sink is not null && sink.TrySetResult(result);
    }

    /// <summary>Tier 2 only: resolve the caller's ValueTask after a successful <see cref="TryClaimForReconciliation"/>.</summary>
    public void CompleteClaimed(RedisResult result) => _core.SetResult(result);

    /// <summary>Tier 2 only: resolve the caller's ValueTask after a successful <see cref="TryClaimForReconciliation"/>.</summary>
    public void CompleteClaimedWithException(Exception exception) => _core.SetException(exception);

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
        _reconciliationSink = null;
    }

    RedisResult IValueTaskSource<RedisResult>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<RedisResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<RedisResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
