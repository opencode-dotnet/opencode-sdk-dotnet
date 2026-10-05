using System.ComponentModel;
using System.Diagnostics;
using System.IO.Abstractions;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// A process held open from while it still runs, so that its end is observed as the operating
/// system completes it. On Windows, <see cref="Process.HasExited"/> — and with it
/// <c>Process.WaitForExitAsync</c> and <see cref="Process.GetProcessById(int)"/>
/// — reports a process gone as soon as its exit code is set, while the handles it holds, its open
/// files included, close afterwards; a run root deleted in that gap keeps whatever the dying
/// process still held (measured: a killed source-run daemon's <c>data/</c> SQLite files and its
/// <c>home/</c> working directory). The process object is signalled only once that is done, so on
/// Windows this waits on the handle it opened while the process ran, which also pins the pid to
/// this process. Elsewhere a process's descriptors close as it exits, and the exit is awaited.
/// </summary>
internal sealed class HeldProcess : IDisposable
{
    private readonly Process _process;

    private HeldProcess(Process process) => _process = process;

    /// <summary>
    /// Holds the process a mark names, or returns null when the pid no longer names that process.
    /// </summary>
    /// <param name="fileSystem">The filesystem <c>/proc</c> is read through.</param>
    /// <param name="mark">The process to hold.</param>
    /// <returns>The held process, or null when it is already gone or was never readable.</returns>
    public static HeldProcess? TryHold(IFileSystem fileSystem, ProcessMark mark)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(mark.ProcessId);
            if (OperatingSystem.IsWindows())
            {
                // Opened before the identity check, so a pid reused in between is caught by it.
                _ = process.SafeHandle;
            }

            if (ProcessMark.TryRead(fileSystem, mark.ProcessId) == mark)
            {
                var held = new HeldProcess(process);
                process = null;
                return held;
            }

            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // No process runs under the pid any more, or its handle is refused: nothing to hold.
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>Holds every marked process that still runs.</summary>
    /// <param name="fileSystem">The filesystem <c>/proc</c> is read through.</param>
    /// <param name="marks">The processes to hold.</param>
    /// <returns>The held processes; each is the caller's to release.</returns>
    public static List<HeldProcess> HoldAll(IFileSystem fileSystem, IEnumerable<ProcessMark> marks) =>
        [.. marks.Select(mark => TryHold(fileSystem, mark)).OfType<HeldProcess>()];

    /// <summary>Releases every held process.</summary>
    /// <param name="held">The processes to release.</param>
    public static void ReleaseAll(IEnumerable<HeldProcess> held)
    {
        foreach (var process in held)
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Ends the tree of every marked process that still runs, then waits at most
    /// <paramref name="bound"/> for each of them to be finished. Every process is held before any is
    /// ended, so none can be missed between its end and the wait for it.
    /// </summary>
    /// <param name="fileSystem">The filesystem <c>/proc</c> is read through.</param>
    /// <param name="marks">The processes to end.</param>
    /// <param name="bound">How long to wait for each.</param>
    /// <returns>True when every process that still ran finished inside the bound.</returns>
    public static async Task<bool> EndAllAsync(IFileSystem fileSystem, IEnumerable<ProcessMark> marks, TimeSpan bound)
    {
        var held = HoldAll(fileSystem, marks);
        try
        {
            foreach (var process in held)
            {
                _ = ProcessTreeTerminator.Platform.TryKill(process._process);
            }

            var finished = true;
            foreach (var process in held)
            {
                finished &= await process.WaitForTerminationAsync(bound, CancellationToken.None).ConfigureAwait(false);
            }

            return finished;
        }
        finally
        {
            ReleaseAll(held);
        }
    }

    /// <summary>Waits at most <paramref name="bound"/> for the held process to be finished.</summary>
    /// <param name="bound">How long to wait.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True when the process finished inside the bound.</returns>
    public async Task<bool> WaitForTerminationAsync(TimeSpan bound, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await ProcessObservation.ObserveExitWithinAsync(_process, bound, cancellationToken).ConfigureAwait(false);
        }

        using var signal = new ProcessSignal(_process.SafeHandle);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            signal,
            (_, timedOut) => completion.TrySetResult(!timedOut),
            state: null,
            bound,
            executeOnlyOnce: true);
        try
        {
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _ = registration.Unregister(null);
        }
    }

    public void Dispose() => _process.Dispose();

    /// <summary>The held process handle as a wait handle; the process keeps ownership of it.</summary>
    private sealed class ProcessSignal : WaitHandle
    {
        public ProcessSignal(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
