using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Windows.Abstractions;

/// <summary>
/// One child's exit as a watch observes it. Disposal releases the watch: at once when the child
/// has exited, otherwise when it does, so the process handle keeps the child's pid from being
/// reused for as long as the child runs.
/// </summary>
internal interface IWindowsExitWatch : IDisposable
{
    /// <summary>Gets the child's own exit: its full 32-bit code, or an unknown status when the code could not be read.</summary>
    public Task<ChildExitStatus> Exited { get; }
}
