using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Diagnostics;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The shipped <see cref="IWindowsExitWatch"/>: a native registered wait on a duplicate of the
/// process handle, the way libuv waits for a Windows child (<c>RegisterWaitForSingleObject</c> with
/// <c>WT_EXECUTEINWAITTHREAD | WT_EXECUTEONLYONCE</c>). The operating system's wait threads each
/// wait on many handles, so no thread is held per child, and the callback runs on the wait thread
/// itself: it never waits in the .NET thread pool's queue, so a busy pool cannot hold back an
/// exit the kernel has already reported. Once the handle signals, the code comes from
/// <c>GetExitCodeProcess</c> in its full 32 bits (<c>0xC0000005</c> reads as <c>-1073741819</c>,
/// the bits <c>Process.ExitCode</c> gives), so a code that happens to equal <c>STILL_ACTIVE</c> is
/// never mistaken for a running child. Two parties must both arrive before the registration, the
/// wait handle, and the process handle are released: the exit, and the watch's disposal. Each
/// arrival sets its own bit once and for good, and the one arrival that finds the other's bit
/// already set releases, so the release runs exactly once whatever the order and however often the
/// watch is disposed.
/// </summary>
/// <remarks>
/// The callback is the one place native code calls into the SDK, so it stays minimal and never
/// blocks the wait thread: the exit code, its arrival, and the task's completion, whose
/// continuations run asynchronously. Nothing escapes it: an exception crossing back into native
/// code ends the process, so a failed read completes the exit as unknown and the arrival still
/// happens. The release unregisters with <c>UnregisterWaitEx</c> and <c>INVALID_HANDLE_VALUE</c>,
/// which returns only once a callback still running has finished, and closes the handles after it.
/// The callback cannot wait for itself, so when its arrival is the releasing one it hands the
/// release to the pool, where that wait is legal; no handle closes while the callback runs.
/// </remarks>
internal sealed class WindowsExitWatch : IWindowsExitWatch
{
    private const int ExitArrived = 1;
    private const int DisposalArrived = 2;
    private const int BothArrived = ExitArrived | DisposalArrived;

#if !NET
    /// <summary>The callback for the downlevel targets, alive for the process's lifetime, so no registration outlives it.</summary>
    private static readonly WaitCallback Callback = OnSignaledFromNative;

    private static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
#endif

    private static int s_liveWatches;

    private readonly SafeProcessHandle _process;
    private readonly SafeWaitHandle _signal;
    private readonly IExitWaitKernel _kernel;
    private readonly TaskCompletionSource<ChildExitStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _self;
    private IntPtr _registration;
    private int _arrivals;
    private int _released;
    private int _releaseFailed;

    private WindowsExitWatch(SafeProcessHandle process, SafeWaitHandle signal, IExitWaitKernel kernel)
    {
        _process = process;
        _signal = signal;
        _kernel = kernel;
    }

#if !NET
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WaitCallback(IntPtr context, byte timedOut);
#endif

    /// <summary>
    /// Gets how many watches of this process have not been released yet: a watch is released once
    /// its child exited and the watch was disposed, so a count left at the end of a test session
    /// names a child nobody ended or a watch nobody disposed.
    /// </summary>
    public static int LiveWatches => Volatile.Read(ref s_liveWatches);

    /// <inheritdoc />
#pragma warning disable VSTHRD003 // The registered wait completes this exit, asynchronously to its continuations; exposing it is the watch's contract.
    public Task<ChildExitStatus> Exited => _exited.Task;
#pragma warning restore VSTHRD003

    /// <summary>Gets whether this watch has released its registration and handles.</summary>
    internal bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>
    /// Gets whether the release found the wait could not be unregistered, and so kept the handles
    /// and the callback's context rather than free what a callback could still reach.
    /// </summary>
    internal bool ReleaseFailed => Volatile.Read(ref _releaseFailed) != 0;

    /// <summary>
    /// Gets whether the thread that completed <see cref="Exited"/> was a .NET thread-pool thread:
    /// false on the native wait thread, which is the watch's contract; null before the exit.
    /// </summary>
    internal bool? CompletedOnPoolThread { get; private set; }

    /// <summary>Starts watching a child; the watch owns its process handle from here.</summary>
    /// <param name="process">The child's process handle.</param>
    /// <returns>The running watch.</returns>
    /// <exception cref="Win32Exception">The handle could not be duplicated for the wait, or the wait could not be registered; the caller still owns the process handle.</exception>
    public static WindowsExitWatch Start(SafeProcessHandle process) => Start(process, ExitWaitKernel.Instance);

    /// <summary>Starts watching a child through the given kernel calls.</summary>
    /// <param name="process">The child's process handle.</param>
    /// <param name="kernel">The kernel calls the watch makes.</param>
    /// <returns>The running watch.</returns>
    /// <exception cref="Win32Exception">The handle could not be duplicated for the wait, or the wait could not be registered; the caller still owns the process handle.</exception>
    internal static WindowsExitWatch Start(SafeProcessHandle process, IExitWaitKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(kernel);

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

        var watch = new WindowsExitWatch(process, waitable, kernel);
        var registration = IntPtr.Zero;
        try
        {
            watch._self = GCHandle.Alloc(watch);

            // The callback may run before this call returns, when the child has already exited;
            // it reads nothing the registration's result sets, and the release that needs the
            // result waits for the watch's disposal, which cannot come before this method returns.
            if (!kernel.Register(
                    out registration,
                    waitable,
                    CallbackAddress(),
                    GCHandle.ToIntPtr(watch._self),
                    WindowsInterop.ExecuteInWaitThread | WindowsInterop.ExecuteOnlyOnce))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Volatile.Write(ref watch._registration, registration);
        }
        catch
        {
            // Until the watch is handed over, nothing else owns the registration, the callback's
            // context, or the duplicate, so a failure releases them here and the process handle
            // stays the caller's. A registration that cannot be unregistered may still call back,
            // so its context and duplicate are then kept rather than freed under it. A thread
            // abort on .NET Framework is not covered.
            if (registration == IntPtr.Zero || kernel.Unregister(registration))
            {
                if (watch._self.IsAllocated)
                {
                    watch._self.Free();
                }

                waitable.Dispose();
            }

            throw;
        }

        // Counted only once registered: the release that uncounts it needs the watch's disposal,
        // which cannot come before this returns.
        _ = Interlocked.Increment(ref s_liveWatches);
        return watch;
    }

    /// <summary>
    /// Releases the watch now when the child has exited; otherwise the exit releases it when the
    /// child exits. A second disposal does nothing. The task keeps its result either way.
    /// </summary>
    public void Dispose()
    {
        // A disposal that completes both arrivals releases on the caller's thread, and the release
        // waits for the callback to finish. That thread is never the wait thread the callback runs
        // on: the callback completes the task with its continuations run asynchronously, so no
        // caller code runs inside it, and nothing in the callback disposes the watch.
        if (Arrive(DisposalArrived))
        {
            Release();
        }
    }

#if NET
    // The address of an unmanaged-callers-only method is a function pointer, which only an unsafe
    // context can take; this is the SDK's one reverse call from native code, so the unsafe context
    // is this one expression, and the trim- and AOT-safe callback is worth it over a delegate.
#pragma warning disable S6640
    private static unsafe IntPtr CallbackAddress() =>
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte, void>)&OnSignaledFromNative;
#pragma warning restore S6640

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
#else
    private static IntPtr CallbackAddress() => CallbackPointer;
#endif

    [SlopwatchSuppress(
        "SW003",
        "The native callback's boundary: an exception that crossed back into the operating system's wait thread would end the process, so every failure completes the exit as unknown instead.")]
    private static void OnSignaledFromNative(IntPtr context, byte timedOut)
    {
        WindowsExitWatch? watch = null;
        try
        {
            watch = GCHandle.FromIntPtr(context).Target as WindowsExitWatch;
            watch?.OnExited();
        }
        catch (Exception)
        {
            // Nothing may cross back into native code; the exit itself is certain.
            _ = watch?._exited.TrySetResult(ChildExitStatus.Unknown);
        }
    }

    [SlopwatchSuppress(
        "SW003",
        "The read runs on the wait thread, where nothing may escape: a read that fails in any way reports the exit as unknown, and the exit's arrival below still happens, so the watch is still released.")]
    private void OnExited()
    {
        var status = ChildExitStatus.Unknown;
        try
        {
            if (_kernel.TryReadExitCode(_process, out var code))
            {
                status = new ChildExitStatus { ExitCode = unchecked((int)code) };
            }
        }
        catch (Exception)
        {
            // The code is unreadable; the exit itself is certain.
        }

        // The exit arrives before the task completes, so a caller that saw the exit and then
        // disposes the watch always finds it arrived and releases at once.
        if (Arrive(ExitArrived))
        {
            // The callback cannot wait for its own end, so the release runs on the pool, where the
            // unregistration waits for this callback to return before anything is closed.
            _ = ThreadPool.UnsafeQueueUserWorkItem(static state => (state as WindowsExitWatch)?.ReleaseOnPool(), this);
        }

        CompletedOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
        _ = _exited.TrySetResult(status);
    }

    /// <summary>
    /// Records one party's arrival, once: true for the arrival that completes both, which is the
    /// one that releases; false for the first arrival and for any repeat of either.
    /// </summary>
    private bool Arrive(int party)
    {
        var current = Volatile.Read(ref _arrivals);
        while (true)
        {
            if ((current & party) != 0)
            {
                return false;
            }

            var observed = Interlocked.CompareExchange(ref _arrivals, current | party, current);
            if (observed == current)
            {
                return (current | party) == BothArrived;
            }

            current = observed;
        }
    }

    /// <summary>The release as the pool runs it, where an exception would end the host.</summary>
    [SlopwatchSuppress(
        "SW003",
        "A pool work item that throws ends the host, so a release that fails there keeps what it holds rather than escape; the release itself already keeps everything a callback could still reach when the unregistration fails.")]
    private void ReleaseOnPool()
    {
        try
        {
            Release();
        }
        catch (Exception)
        {
            // Nothing may escape a pool work item; what the release did not close stays open.
            Volatile.Write(ref _releaseFailed, 1);
        }
    }

    private void Release()
    {
        // Returns once a callback still running has finished, so nothing below races it. A wait
        // that cannot be unregistered may still call back, so its handles and context are kept:
        // leaking a registration is safe, freeing what a live callback reaches is not.
        if (_kernel.Unregister(Volatile.Read(ref _registration)))
        {
            _signal.Dispose();
            _process.Dispose();
            _self.Free();
            Volatile.Write(ref _released, 1);
        }
        else
        {
            Volatile.Write(ref _releaseFailed, 1);
        }

        // The release has run either way: nothing more is left for the watch to do.
        _ = Interlocked.Decrement(ref s_liveWatches);
    }
}
