namespace ForgeDesk.Core.GitHub;

internal static class AbandonableTask
{
    /// <summary>
    /// Waits for <paramref name="task"/> unless <paramref name="cancellationToken"/> fires first.
    /// Most Octokit calls accept no token, so on cancellation the request is abandoned rather
    /// than aborted; its eventual failure is observed so it isn't reported as an unobserved
    /// task exception.
    /// </summary>
    public static async Task<T> WaitOrAbandonAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !task.IsCompleted)
        {
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }
}
