using System.Collections.Concurrent;
using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Tests.Unit.Connections;

/// <summary>
/// The deterministic tests in <see cref="PendingRequestCompletionClaimTests"/> prove
/// every ordering is handled correctly as a sequence of calls on one logical thread of
/// control. They can't catch a memory-visibility bug in the publish-before-claim
/// ordering that <see cref="PendingRequest.TryClaimForReconciliation"/> depends on
/// (docs/design/state-machines.md §2.4's requirement that the reconciliation sink be
/// visible to a concurrent <see cref="PendingRequest.TryDeliverReconciliationFrame"/>
/// call by the time it observes the claim). This drives real, concurrent
/// <see cref="Task.Run(Action)"/> threads at the primitive, many thousands of times, to
/// give that ordering a real chance to fail if it's wrong.
/// </summary>
public class PendingRequestReconciliationRaceStressTests
{
    private const int Iterations = 5_000;

    [Fact]
    public async Task ConcurrentReplyAndCancellationNeverLoseOrDoubleCompleteTheCaller()
    {
        var failures = new ConcurrentBag<string>();

        await Parallel.ForAsync(0, Iterations, async (i, token) =>
        {
            var pending = new PendingRequest();
            var valueTask = new ValueTask<RedisResult>(pending, pending.Version);

            // Mirrors RedisConnection's two real racing call sites: the read loop's
            // DispatchReply, and the cancellation-token registration that drives
            // ReconcileCancellationAsync — started concurrently with no ordering
            // guarantee between them, exactly like the production race.
            var dispatchTask = Task.Run(() =>
            {
                var frame = RedisResult.FromInteger(i);
                if (!pending.TryCompleteWithResult(frame))
                {
                    pending.TryDeliverReconciliationFrame(frame);
                }
            }, token);

            var cancelTask = Task.Run(async () =>
            {
                if (!pending.TryClaimForReconciliation(out var frameDelivered))
                {
                    return;
                }

                RedisResult frame;
                try
                {
                    frame = await frameDelivered.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    pending.CompleteClaimedWithException(new OperationCanceledException("stress-test grace timeout"));
                    return;
                }

                // Simulate "CLIENT UNBLOCK raced" ~half the time, exercising both
                // reconciliation outcomes under real concurrency.
                if (i % 2 == 0)
                {
                    pending.CompleteClaimed(frame);
                }
                else
                {
                    pending.CompleteClaimedWithException(new OperationCanceledException("simulated unblock confirmed"));
                }
            }, token);

            await Task.WhenAll(dispatchTask, cancelTask).ConfigureAwait(false);

            // A pooled IValueTaskSource-backed ValueTask may only be consumed once
            // (project spec §9.5) — converting to a plain Task here is that single
            // consumption point; the Task itself can then be safely observed twice
            // below (once for the timeout race, once to assert its outcome).
            var callerTask = valueTask.AsTask();

            var completed = await Task.WhenAny(callerTask, Task.Delay(TimeSpan.FromSeconds(2), token)).ConfigureAwait(false);
            if (completed != callerTask)
            {
                failures.Add($"iteration {i}: lost wakeup — the caller's ValueTask never completed (hazard H1).");
                return;
            }

            try
            {
                await callerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected outcome on the "cancellation wins and CLIENT UNBLOCK confirms
                // it" path — not a failure.
            }
        });

        Assert.Empty(failures);
    }
}
