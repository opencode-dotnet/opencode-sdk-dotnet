using Microsoft.Win32.SafeHandles;

namespace OpenCode.Sdk.Internal;

/// <summary>The shipped <see cref="IWindowsChildExit"/>: a <see cref="WindowsExitWatch"/> per child.</summary>
internal sealed class WindowsChildExit : IWindowsChildExit
{
    /// <inheritdoc />
    public IWindowsExitWatch Watch(SafeProcessHandle process) => WindowsExitWatch.Start(process);
}
