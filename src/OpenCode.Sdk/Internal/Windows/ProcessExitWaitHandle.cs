using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// A wait handle over a process handle, the way .NET's own process wait handle is built: the handle
/// is set once, in the constructor. An event wait handle with its handle swapped instead would leak
/// the event it created, because the setter does not close the handle it replaces.
/// </summary>
internal sealed class ProcessExitWaitHandle : WaitHandle
{
    /// <summary>Initializes the wait handle; it owns <paramref name="handle"/> from here.</summary>
    /// <param name="handle">A handle with the <c>SYNCHRONIZE</c> right on the process.</param>
    public ProcessExitWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
}
