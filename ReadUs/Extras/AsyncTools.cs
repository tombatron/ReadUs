using System;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs.Extras;

internal static class AsyncTools
{
    internal static async Task WaitWhileAsync(Func<bool> condition, CancellationToken cancellationToken,
        int pollDelayMs = 25)
    {
        try
        {
            while (condition())
                await Task.Delay(TimeSpan.FromMilliseconds(pollDelayMs), cancellationToken).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            // This is the only exception that we're going to swallow...
        }
    }
}