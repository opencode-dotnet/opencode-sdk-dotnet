using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Diagnostics;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The shipped <see cref="IWindowsExitWatch"/>: a registered thread-pool wait on a duplicate of the
/// process handle, the way libuv waits for a Windows child. The pool's wait threads each wait on
/// many handles, so no thread is held per child. Once the handle signals, the code comes from
/// <c>GetExitCodeProcess</c> in its full 32 bits (<c>0xC0000005</c> reads as <c>-1073741819</c>,
/// the bits <c>Process.ExitCode</c> gives), so a code that happens to equal <c>STILL_ACTIVE</c> is
/// never mistaken for a running child. Two parties must both be done before the registration, the
/// wait handle, and the process handle are released: the exit, and the watch's disposal. One
/// interlocked state records which came first, and whichever comes second releases; the exit
/// callback reads the code before it marks its arrival, so it never touches a released handle.
/// </summary>
internal sealed class WindowsExitWatch : IWindowsExitWatch
{
    private const int Running = 0;
    private const int ExitObserved = 1;
    private const int Disposed = 2;

    private static int s_liveWatches;

    private readonly SafeProcessHandle _process;
    private readonly ProcessExitWaitHandle _signal;
    private readonly TaskCompletionSource<ChildExitStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RegisteredWaitHandle? _registration;
    private int _state = Running;

    private WindowsExitWatch(SafeProcessHandle process, ProcessExitWaitHandle signal)
    {
        _process = process;
        _signal = signal;
    }

    /// <summary>
    /// Gets how many watches of this process have not been released yet: a watch is released once
    /// its child exited and the watch was disposed, so a count left at the end of a test session
    /// names a child nobody ended or a watch nobody disposed.
    /// </summary>
    public static int LiveWatches => Volatile.Read(ref s_liveWatches);

    /// <inheritdoc />
    public Task<ChildExitStatus> Exited => _exited.Task;

    /// <summary>Starts watching a child; the watch owns its process handle from here.</summary>
    /// <param name="process">The child's process handle.</param>
    /// <returns>The running watch.</returns>
    /// <exception cref="Win32Exception">The handle could not be duplicated for the wait; the caller still owns it, as it does when the wait cannot be registered.</exception>
    public static WindowsExitWatch Start(SafeProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!WindowsInterop.DuplicateProcessHandle(
                WindowsInterop.CurrentProcess,
                process,
                WindowsInterop.CurrentProcess,
                out var waitable,
                WindowsInterop.Synchronize,
                inheritHandle: false,
                0))
        {
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            waitable.Dispose();
            throw failure;
        }

        var watch = new WindowsExitWatch(process, new ProcessExitWaitHandle(waitable));
        try
        {
            Volatile.Write(
                ref watch._registration,
                ThreadPool.RegisterWaitForSingleObject(
                    watch._signal,
                    static (state, _) => (state as WindowsExitWatch)?.OnExited(),
                    watch,
                    Timeout.Infinite,
                    executeOnlyOnce: true));
        }
        catch
        {
            // Nothing waits on the duplicate, so it is closed here; the process handle stays the caller's.
            watch._signal.Dispose();
            throw;
        }

        // Counted only once registered: the release that uncounts it needs the watch's disposal,
        // which cannot come before this returns.
        _ = Interlocked.Increment(ref s_liveWatches);
        return watch;
    }

    /// <summary>
    /// Releases the watch now when the child has exited; otherwise the exit callback releases it
    /// when the child exits. The task keeps its result either way.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, Disposed) == ExitObserved)
        {
            Release();
        }
    }

    [SlopwatchSuppress(
        "SW003",
        "A handle closed under the callback is the one way the code becomes unreadable; the exit is then reported as unknown rather than raised on a pool thread, where it would end the host.")]
    private void OnExited()
    {
        ChildExitStatus status;
        try
        {
            status = WindowsInterop.GetExitCodeProcess(_process, out var code)
                ? new ChildExitStatus { ExitCode = unchecked((int)code) }
                : ChildExitStatus.Unknown;
        }
        catch (ObjectDisposedException)
        {
            // The code is no longer readable; the exit itself is certain.
            status = ChildExitStatus.Unknown;
        }

        // The exit is marked before the task completes, so a caller that saw the exit and then
        // disposes the watch always finds it marked and releases at once.
        if (Interlocked.Exchange(ref _state, ExitObserved) == Disposed)
        {
            Release();
        }

        _ = _exited.TrySetResult(status);
    }

    private void Release()
    {
        // No wait handle to signal: the parameter is nullable in the reference assemblies but not in
        // every implementation assembly a compilation may bind against.
        _ = Volatile.Read(ref _registration)?.Unregister(null!);
        _signal.Dispose();
        _process.Dispose();
        _ = Interlocked.Decrement(ref s_liveWatches);
    }
}
