using System.Diagnostics;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Process-truth observation for launcher tests: whether a process identifier is still alive, and
/// a bounded wait for it to stop being. An absent process is the state these wait for, so a
/// process that is already gone answers immediately rather than failing the arrangement.
/// </summary>
internal static class ProcessObservation
{
    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process child;
        try
        {
            child = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (child)
        {
            await child.WaitForExitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Waits at most <paramref name="bound"/> for the process to exit. A whole-tree kill is
    /// asynchronous, so a descendant is observed to terminate inside a bound of the test's own
    /// rather than asserted gone at the instant disposal returns.
    /// </summary>
    /// <returns>True when the process exited inside the bound.</returns>
    public static Task<bool> ObserveExitWithinAsync(
        int processId, TimeSpan bound, CancellationToken cancellationToken) =>
        WithinAsync(token => WaitForExitAsync(processId, token), bound, cancellationToken);

    /// <summary>Waits at most <paramref name="bound"/> for an owned process to exit.</summary>
    /// <returns>True when the process exited inside the bound.</returns>
    public static Task<bool> ObserveExitWithinAsync(
        Process process, TimeSpan bound, CancellationToken cancellationToken) =>
        WithinAsync(process.WaitForExitAsync, bound, cancellationToken);

    /// <summary>Ends a process tree when the pid still names a live process; a pid already gone is left alone.</summary>
    [SlopwatchSuppress(
        "SW003",
        "Best-effort teardown: the pid being gone is the state the test is after, so a GetProcessById ArgumentException is the success path, not a swallowed failure.")]
    public static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            _ = TestProcessTreeKill.TryKill(process);
        }
        catch (ArgumentException)
        {
            // The pid being gone is the state teardown is after.
        }
    }

    private static async Task<bool> WithinAsync(Func<CancellationToken, Task> wait, TimeSpan bound, CancellationToken cancellationToken)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        observation.CancelAfter(bound);
        try
        {
            await wait(observation.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
