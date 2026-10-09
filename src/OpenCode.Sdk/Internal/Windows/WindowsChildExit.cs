using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>The shipped <see cref="IWindowsChildExit"/>: a <see cref="WindowsExitWatch"/> per child.</summary>
internal sealed class WindowsChildExit : IWindowsChildExit
{
    /// <inheritdoc />
    public IWindowsExitWatch Watch(SafeProcessHandle process) => WindowsExitWatch.Start(process);
}
