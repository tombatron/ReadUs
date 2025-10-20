namespace ReadUs.Extras;

public static class AsyncTools
{
    public static async Task WaitWhileAsync(Func<bool> condition, CancellationToken cancellationToken, int pollDelayMs = 25)
    {
        try
        {
            while (condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(pollDelayMs), cancellationToken).ConfigureAwait(true);
            }
                
        }
        catch (TaskCanceledException)
        {
            // This is the only exception that we're going to swallow...
        }
    }
}