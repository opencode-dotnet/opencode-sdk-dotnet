using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A C library without <c>posix_spawn_file_actions_addchdir_np</c>, as glibc before 2.29 is: the
/// spawn has to start a child in its working directory through <c>env -C</c> instead.
/// </summary>
internal sealed class MissingWorkingDirectoryAction : IWorkingDirectoryAction
{
    private int _asked;

    /// <summary>Gets how many times a spawn asked for the action.</summary>
    public int Asked => Volatile.Read(ref _asked);

    public bool TryAdd(IntPtr fileActions, string directory)
    {
        _ = Interlocked.Increment(ref _asked);
        return false;
    }
}
