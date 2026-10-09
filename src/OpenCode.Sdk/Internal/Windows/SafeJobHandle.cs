using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// A job object handle. Closing the last handle of a kill-on-close job ends every process in it,
/// so the launcher's job is held in one of these for the life of the process and never disposed:
/// the kernel closes it when the process ends, which is how the servers end with their owner.
/// </summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>Initializes an empty handle, for the interop marshaller.</summary>
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => WindowsInterop.CloseHandle(handle);
}
