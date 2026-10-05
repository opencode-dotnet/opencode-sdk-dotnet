using System.ComponentModel;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// The shipped <see cref="IWorkingDirectoryAction"/>. Whether the C library exports the action is
/// learned from the first call: the binding resolves its entry point on first use and throws
/// <see cref="EntryPointNotFoundException"/> when there is none, the same way on every target
/// framework, so no library probing is needed.
/// </summary>
internal sealed class WorkingDirectoryAction : IWorkingDirectoryAction
{
    /// <summary>The missing export cannot appear later in the same process, so it is remembered.</summary>
    private volatile bool _missing;

    /// <inheritdoc />
    public bool TryAdd(IntPtr fileActions, string directory)
    {
        if (_missing)
        {
            return false;
        }

        int result;
        try
        {
            result = PosixInterop.AddChangeDirectoryAction(fileActions, directory);
        }
        catch (EntryPointNotFoundException)
        {
            _missing = true;
            return false;
        }

        return result == 0 ? true : throw new Win32Exception(result);
    }
}
