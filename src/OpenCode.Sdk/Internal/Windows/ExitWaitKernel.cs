using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>The shipped <see cref="IExitWaitKernel"/>: the kernel32 calls themselves.</summary>
internal sealed class ExitWaitKernel : IExitWaitKernel
{
    /// <summary>Gets the one instance; it holds no state.</summary>
    public static ExitWaitKernel Instance { get; } = new();

    /// <inheritdoc />
    public bool Register(out IntPtr registration, SafeWaitHandle waited, IntPtr callback, IntPtr context, uint flags) =>
        WindowsInterop.RegisterWaitForSingleObject(out registration, waited, callback, context, WindowsInterop.InfiniteWait, flags);

    /// <inheritdoc />
    public void Unregister(IntPtr registration) =>
        _ = WindowsInterop.UnregisterWaitEx(registration, WindowsInterop.InvalidHandleValue);

    /// <inheritdoc />
    public bool TryReadExitCode(SafeProcessHandle process, out uint exitCode) =>
        WindowsInterop.GetExitCodeProcess(process, out exitCode);
}
