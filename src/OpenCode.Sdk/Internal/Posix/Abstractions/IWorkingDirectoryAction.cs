namespace OpenCode.Sdk.Internal.Posix.Abstractions;

/// <summary>
/// The spawn file action that starts a child in another directory,
/// <c>posix_spawn_file_actions_addchdir_np</c>. A C library older than the export (glibc before
/// 2.29) has no such action; the spawn then starts the child through <c>env -C</c> instead.
/// </summary>
internal interface IWorkingDirectoryAction
{
    /// <summary>Adds the directory change to a file-action list.</summary>
    /// <param name="fileActions">The initialized <c>posix_spawn_file_actions_t</c>.</param>
    /// <param name="directory">The directory the child starts in.</param>
    /// <returns>False when the C library has no such action; nothing was added.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The C library refused the action; its error code is the errno.</exception>
    public bool TryAdd(IntPtr fileActions, string directory);
}
