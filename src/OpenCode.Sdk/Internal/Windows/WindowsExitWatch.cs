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
/// never mistaken for a running child. Two parties must both be done before the registration, the
/// wait handle, and the process handle are released: the exit, and the watch's disposal. One
/// interlocked state records which came first, and whichever comes second releases; the exit
/// callback reads the code before it marks its arrival, so it never touches a released handle.
/// </summary>
/// <remarks>
/// The callback is the one place native code calls into the SDK, so it stays minimal and never
/// blocks the wait thread: the exit code, the state change, and the task's completion, whose
/// continuations run asynchronously. Nothing escapes it: an exception crossing back into native
/// code ends the process, so a failure completes the exit as unknown. A release outside the
/// callback unregisters with <c>UnregisterWaitEx</c> and <c>INVALID_HANDLE_VALUE</c>, which returns
/// once a callback still running has finished; a release inside the callback cannot wait for
/// itself, so it unregisters with the non-blocking <c>UnregisterWait</c> and leaves the closing of
/// the handles to the pool, after the callback has returned.
/// </remarks>
internal sealed class WindowsExitWatch : IWindowsExitWatch
{
    private const int Running = 0;
    private const int ExitObserved = 1;
    private const int Disposed = 2;

#if !NET
    /// <summary>The callback for the downlevel targets, alive for the process's lifetime, so no registration outlives it.</summary>
    private static readonly WaitCallback Callback = OnSignaledFromNative;

    private static readonly IntPtr CallbackPointer = Marshal.GetFunctionPointerForDelegate(Callback);
#endif

    private static int s_liveWatches;

    private readonly SafeProcessHandle _process;
    private readonly SafeWaitHandle _signal;
    private readonly TaskCompletionSource<ChildExitStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _self;
    private IntPtr _registration;
    private int _state = Running;
    private int _released;

    private WindowsExitWatch(SafeProcessHandle process, SafeWaitHandle signal)
    {
        _process = process;
        _signal = signal;
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
    /// Gets whether the thread that completed <see cref="Exited"/> was a .NET thread-pool thread:
    /// false on the native wait thread, which is the watch's contract; null before the exit.
    /// </summary>
    internal bool? CompletedOnPoolThread { get; private set; }

    /// <summary>Starts watching a child; the watch owns its process handle from here.</summary>
    /// <param name="process">The child's process handle.</param>
    /// <returns>The running watch.</returns>
    /// <exception cref="Win32Exception">The handle could not be duplicated for the wait, or the wait could not be registered; the caller still owns the process handle.</exception>
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

        var watch = new WindowsExitWatch(process, waitable);
        watch._self = GCHandle.Alloc(watch);

        // The callback may run before this call returns, when the child has already exited; it
        // reads nothing the registration's result sets, and the release that needs the result
        // waits for the watch's disposal, which cannot come before this method returns.
        if (!WindowsInterop.RegisterWaitForSingleObject(
                out var registration,
                waitable,
                CallbackAddress(),
                GCHandle.ToIntPtr(watch._self),
                WindowsInterop.InfiniteWait,
                WindowsInterop.ExecuteInWaitThread | WindowsInterop.ExecuteOnlyOnce))
        {
            // Nothing waits on the duplicate, so it is closed here; the process handle stays the caller's.
            var failure = new Win32Exception(Marshal.GetLastWin32Error());
            watch._self.Free();
            waitable.Dispose();
            throw failure;
        }

        Volatile.Write(ref watch._registration, registration);

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
            Release(insideCallback: false);
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
        "A handle closed under the callback is the one way the code becomes unreadable; the exit is then reported as unknown rather than raised on the wait thread, where it would end the host.")]
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
            Release(insideCallback: true);
        }

        CompletedOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
        _ = _exited.TrySetResult(status);
    }

    private void Release(bool insideCallback)
    {
        var registration = Volatile.Read(ref _registration);
        if (!insideCallback)
        {
            // Returns once a callback still running has finished, so nothing below races it.
            _ = WindowsInterop.UnregisterWaitEx(registration, WindowsInterop.InvalidHandleValue);
            CloseHandles();
            return;
        }

        // This callback is the one still running, so the unregistration completes when it returns;
        // the handles close after that, on the pool, where a late close holds nothing back.
        _ = WindowsInterop.UnregisterWait(registration);
        _ = ThreadPool.UnsafeQueueUserWorkItem(static state => (state as WindowsExitWatch)?.CloseHandles(), this);
    }

    private void CloseHandles()
    {
        _signal.Dispose();
        _process.Dispose();
        _self.Free();
        Volatile.Write(ref _released, 1);
        _ = Interlocked.Decrement(ref s_liveWatches);
    }
}
