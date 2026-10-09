using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// A thread handle <c>CreateProcessW</c> returned, owned and closed like any other kernel handle.
/// The BCL keeps its own thread handle type internal, so the SDK carries this one; the launcher
/// holds a suspended server's main thread in it until <c>ResumeThread</c>.
/// </summary>
internal sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes an empty handle, for the interop marshaller.</summary>
    public SafeThreadHandle()
        : base(ownsHandle: true)
    {
    }

    /// <summary>Initializes the wrapper over a thread handle this process owns.</summary>
    /// <param name="handle">The raw handle.</param>
    public SafeThreadHandle(IntPtr handle)
        : base(ownsHandle: true) =>
        SetHandle(handle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => WindowsInterop.CloseHandle(handle);
}
