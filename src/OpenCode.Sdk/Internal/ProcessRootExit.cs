using System.ComponentModel;
using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Observes a launched child's own exit, and nothing else. <c>Process.WaitForExitAsync</c> on
/// .NET 8 and later also waits for redirected output read through the event readers to reach
/// end-of-stream, which a surviving descendant holding the pipe can postpone indefinitely; the
/// <see cref="Process.Exited"/> event is raised when the process itself exits, so the launcher's
/// teardown waits on that and bounds the output drain separately.
/// </summary>
internal static class ProcessRootExit
{
    /// <summary>
    /// Reads whether the process has exited. A process this object no longer has an association
    /// with counts as exited; a handle the platform refuses counts as still running, so the caller
    /// escalates rather than assumes.
    /// </summary>
    /// <param name="process">The launched child.</param>
    /// <returns>True when the process has exited.</returns>
    public static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Waits at most <paramref name="window"/> for the process itself to exit.</summary>
    /// <param name="process">The launched child.</param>
    /// <param name="window">How long the caller waits.</param>
    /// <returns>True when the process exited inside the window; false when the window expired first.</returns>
    public static async Task<bool> WaitWithinAsync(Process process, TimeSpan window)
    {
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? sender, EventArgs e) => exited.TrySetResult(true);

        process.Exited += OnExited;
        try
        {
            if (!TryWatch(process) || HasExited(process))
            {
                // Checked after subscribing, so an exit that raced the subscription is not missed.
                return true;
            }

            try
            {
                return await exited.Task.WaitAsync(window).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The window expired with the process still running; the caller decides what follows.
                return false;
            }
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    /// <summary>Turns on the exit notification.</summary>
    /// <returns>False when the process had already exited, so there is nothing to watch.</returns>
    private static bool TryWatch(Process process)
    {
        try
        {
            process.EnableRaisingEvents = true;
            return true;
        }
        catch (InvalidOperationException) when (HasExited(process))
        {
            // The runtime refuses to watch a process that has already exited.
            return false;
        }
    }
}
