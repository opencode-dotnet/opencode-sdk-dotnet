namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>Waits for a task under a bound, reporting an expired bound instead of raising it.</summary>
internal static class BoundedWait
{
    /// <summary>
    /// Waits at most <paramref name="bound"/> for <paramref name="task"/> to end. A task that
    /// faulted or was canceled has ended too: the wait reports that and raises nothing, and how the
    /// task ended stays on the task for its owner to read.
    /// </summary>
    /// <param name="task">The awaited task.</param>
    /// <param name="bound">How long the caller waits.</param>
    /// <returns>True when the task ended inside the bound, however it ended.</returns>
    public static async Task<bool> CompletesWithinAsync(Task task, TimeSpan bound)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.WaitAsync(bound).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            // The bound expired first; the caller continues with what it has.
            return false;
        }
        catch (Exception) when (task.IsCompleted)
        {
            // The task ended, on a failure that belongs to its owner rather than to this wait, or
            // just as the bound expired.
            return true;
        }
    }
}
