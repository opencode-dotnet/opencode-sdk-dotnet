using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal.Windows.Abstractions;

/// <summary>
/// The Windows exit-status seam: observe a child's own exit, with the code the operating system
/// reports in full, without holding a thread while it runs.
/// </summary>
internal interface IWindowsChildExit
{
    /// <summary>Starts watching a child; the watch takes ownership of its process handle.</summary>
    /// <param name="process">The child's process handle, released by the watch once both the exit and the watch's disposal have happened.</param>
    /// <returns>The running watch.</returns>
    /// <exception cref="Win32Exception">The handle could not be prepared for a wait; the caller still owns it.</exception>
    public IWindowsExitWatch Watch(SafeProcessHandle process);
}
