using System.ComponentModel;

namespace OpenCode.Sdk.Internal.Windows.Abstractions;

/// <summary>
/// The Windows spawn seam: <c>CreateProcessW</c> with an explicit inherited-handle list, so the
/// child receives its own standard handles and nothing else the host holds, and with the show state
/// and console flags libuv gives a hidden child (<c>SW_HIDE</c>, and <c>CREATE_NO_WINDOW</c> unless
/// a standard handle is the host's own).
/// </summary>
internal interface IWindowsSpawn
{
    /// <summary>Starts one child.</summary>
    /// <param name="request">What to spawn and where its standard handles lead.</param>
    /// <returns>The child, its handles, and the parent's pipe ends.</returns>
    /// <exception cref="Win32Exception">A pipe, a handle, or the process could not be created; nothing was started, and nothing created here is left open.</exception>
    public WindowsSpawnedChild Spawn(WindowsSpawnRequest request);
}
