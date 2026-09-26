using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Tests.Unit.Connections;

/// <summary>
/// Exhaustive, deterministic coverage of every legal ordering of the completion-claim
/// race described in docs/design/state-machines.md §2.4–§2.6, driving the real
/// <see cref="PendingRequest"/> primitive — not a model of it — through each one. This
/// is the "walk every reachable (state, event) combination" harness project spec §9.1
/// calls for, scoped to the primitive itself (the sharpest, most bug-prone piece); see
/// <see cref="PendingRequestReconciliationRaceStressTests"/> for the companion
/// concurrent-thread stress coverage that exercises the same primitive under real
/// memory-visibility conditions rather than a single logical thread of control.
///
/// There are exactly two events that can race for a given request: R (the read loop
/// dispatches a real reply frame) and C (a caller's cancellation fires). Both orderings
/// are covered below, plus both branches of the Tier 2 reconciliation outcome (the
/// server confirms it unblocked the client, vs. a genuine reply had already beaten the
/// cancellation).
/// </summary>
public class PendingRequestCompletionClaimTests
{
    private static RedisResult SampleResult(string s) => RedisResult.FromBytes(RespType.SimpleString, System.Text.Encoding.ASCII.GetBytes(s));

    // ---- Tier 1 shape: TryCompleteWithResult vs. TryCompleteWithException, both "decide immediately". ----

    [Fact]
    public async Task Tier1_ReplyBeforeCancellation_DeliversTheReply()
    {
        var pending = new PendingRequest();
        var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

        Assert.True(pending.TryCompleteWithResult(SampleResult("R-wins")));
        Assert.False(pending.TryCompleteWithException(new OperationCanceledException())); // loses the claim; must be a no-op, not throw

        var result = await valueTask;
        Assert.Equal("R-wins", result.AsString());
    }

    [Fact]
    public async Task Tier1_CancellationBeforeReply_DeliversCancellation()
    {
        var pending = new PendingRequest();
        var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

        Assert.True(pending.TryCompleteWithException(new OperationCanceledException()));
        Assert.False(pending.TryCompleteWithResult(SampleResult("discarded"))); // loses the claim; the real reply is simply dropped

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await valueTask);
    }

    // ---- Tier 2 shape: cancellation claims first, then must reconcile via CLIENT UNBLOCK before deciding. ----

    [Fact]
    public async Task Tier2_ReplyBeforeCancellation_ClaimForReconciliationFails()
    {
        var pending = new PendingRequest();
        var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

        // Read loop's normal dispatch path.
        Assert.True(pending.TryCompleteWithResult(SampleResult("real-data")));

        // Cancellation registration fires afterward; the claim must fail, matching
        // RedisConnection.ReconcileCancellationAsync's early return.
        Assert.False(pending.TryClaimForReconciliation(out var frameDelivered));
        Assert.True(frameDelivered.IsCanceled, "A failed claim must hand back an already-canceled sentinel task, never a task that could later resolve and be mistaken for a real frame.");

        var result = await valueTask;
        Assert.Equal("real-data", result.AsString());
    }

    [Fact]
    public async Task Tier2_CancellationBeforeReply_UnblockConfirmed_DeliversCancellation()
    {
        var pending = new PendingRequest();
        var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

        // Cancellation claims first.
        Assert.True(pending.TryClaimForReconciliation(out var frameDelivered));

        // Read loop's dispatch: TryCompleteWithResult must lose (already claimed), so
        // production code falls back to handing the frame to the reconciler.
        var realFrame = SampleResult("unblock-artifact");
        Assert.False(pending.TryCompleteWithResult(realFrame));
        Assert.True(pending.TryDeliverReconciliationFrame(realFrame));

        var deliveredFrame = await frameDelivered;
        Assert.Equal("unblock-artifact", deliveredFrame.AsString());

        // Simulated CLIENT UNBLOCK returned 1 (server confirms it actually unblocked
        // the client) -> the frame is the unblock artifact, not real data -> cancel.
        pending.CompleteClaimedWithException(new OperationCanceledException("cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await valueTask);
    }

    [Fact]
    public async Task Tier2_CancellationBeforeReply_UnblockRaced_HonorsTheGenuineReply()
    {
        var pending = new PendingRequest();
        var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

        Assert.True(pending.TryClaimForReconciliation(out var frameDelivered));

        var realFrame = SampleResult("genuine-data");
        Assert.False(pending.TryCompleteWithResult(realFrame));
        Assert.True(pending.TryDeliverReconciliationFrame(realFrame));

        var deliveredFrame = await frameDelivered;

        // Simulated CLIENT UNBLOCK returned 0 (a genuine reply had already beaten the
        // cancellation) -> deliver it, per design doc §2.4's documented policy: a value
        // that already arrived is delivered to the caller even though cancellation was
        // requested.
        pending.CompleteClaimed(deliveredFrame);

        var result = await valueTask;
        Assert.Equal("genuine-data", result.AsString());
    }

    [Fact]
    public async Task Tier2_CancellationWithNoReplyEverArriving_TimesOutAndCancelsCleanly()
    {
        // Models the Discarded transition (design doc §2.5): the reply never shows up
        // (server or connection genuinely gone), so whatever drives reconciliation
        // (RedisConnection's grace-deadline CancellationTokenSource in production) must
        // eventually give up and complete the caller anyway — the request must never be
        // left hanging forever (hazard H1).
        var pending = new PendingRequest();
        var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

        Assert.True(pending.TryClaimForReconciliation(out var frameDelivered));
        Assert.False(frameDelivered.IsCompleted);

        pending.CompleteClaimedWithException(new OperationCanceledException("grace deadline elapsed"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await valueTask);
    }

    [Fact]
    public void SecondClaimAttemptAlwaysLoses_RegardlessOfWhichKindWonFirst()
    {
        // A cross-check that TryCompleteWithResult, TryCompleteWithException and
        // TryClaimForReconciliation all share exactly one claim slot — winning via any
        // one of them must lock out all the others (hazard H2: no double completion).
        var pending = new PendingRequest();
        Assert.True(pending.TryCompleteWithResult(SampleResult("first")));

        Assert.False(pending.TryCompleteWithResult(SampleResult("second")));
        Assert.False(pending.TryCompleteWithException(new InvalidOperationException()));
        Assert.False(pending.TryClaimForReconciliation(out _));
    }

    [Fact]
    public async Task ResetForPoolingAllowsFullReuseWithNoStaleState()
    {
        var pending = new PendingRequest();
        var firstValueTask = new ValueTask<RedisResult>(pending, pending.Version);
        Assert.True(pending.TryCompleteWithResult(SampleResult("first-use")));
        Assert.Equal("first-use", (await firstValueTask).AsString());

        pending.ResetForPooling();

        // A stale reconciliation sink from a prior generation must never leak forward.
        var secondValueTask = new ValueTask<RedisResult>(pending, pending.Version);
        Assert.True(pending.TryClaimForReconciliation(out var frameDelivered));
        Assert.False(frameDelivered.IsCompleted);
        pending.CompleteClaimed(SampleResult("second-use"));
        Assert.Equal("second-use", (await secondValueTask).AsString());
    }
}
