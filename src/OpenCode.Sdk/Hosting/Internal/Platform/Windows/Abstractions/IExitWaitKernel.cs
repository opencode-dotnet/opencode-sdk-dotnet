using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The kernel calls the Windows exit watch makes: the native registered wait, its one
/// unregistration, and the exit-code read. A seam, so a test can see the flags a wait is
/// registered with, count the unregistrations, and make the read fail.
/// </summary>
internal interface IExitWaitKernel
{
    /// <summary>Registers a native wait whose callback runs once the handle signals.</summary>
    /// <param name="registration">The registration, for its unregistration.</param>
    /// <param name="waited">The handle to wait on.</param>
    /// <param name="callback">The callback's address.</param>
    /// <param name="context">The value the callback receives.</param>
    /// <param name="flags">The <c>WT_*</c> flags.</param>
    /// <returns>True when the wait is registered; false with the error in the last Win32 error.</returns>
    public bool Register(out IntPtr registration, SafeWaitHandle waited, IntPtr callback, IntPtr context, uint flags);

    /// <summary>
    /// Unregisters the wait and returns once a callback still running has finished; never called
    /// on the wait thread, where it would wait for itself.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <returns>True when the wait is unregistered and no callback of it can still run.</returns>
    public bool Unregister(IntPtr registration);

    /// <summary>Reads a process's exit code.</summary>
    /// <param name="process">The process.</param>
    /// <param name="exitCode">The code, in its full 32 bits.</param>
    /// <returns>True when the code was read.</returns>
    public bool TryReadExitCode(SafeProcessHandle process, out uint exitCode);
}
