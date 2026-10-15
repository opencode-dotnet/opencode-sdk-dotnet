using System.Runtime.InteropServices;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="IFileRenameKernel"/>: <c>SetFileInformationByHandle(FileRenameInfoEx)</c>
/// with <c>FILE_RENAME_FLAG_REPLACE_IF_EXISTS | FILE_RENAME_FLAG_POSIX_SEMANTICS</c>, on a source
/// handle opened with <c>DELETE</c> access, which the call needs. It is the rename Bun makes in its
/// own install path. The handle is released, and the information buffer freed, on every path.
/// </summary>
internal sealed class FileRenameKernel : IFileRenameKernel
{
    /// <summary><c>MAX_PATH</c>: the length from which a path needs the extended-length prefix.</summary>
    internal const int MaxShortPath = 260;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";
    private const string UncPrefix = @"\\";

    private static readonly int NameOffset = Marshal.OffsetOf<WindowsInterop.FileRenameInformation>(nameof(WindowsInterop.FileRenameInformation.FileName)).ToInt32();
    private static readonly int LengthOffset = Marshal.OffsetOf<WindowsInterop.FileRenameInformation>(nameof(WindowsInterop.FileRenameInformation.FileNameLength)).ToInt32();
    private static readonly int StructureSize = Marshal.SizeOf<WindowsInterop.FileRenameInformation>();

    /// <summary>Gets the one instance; it holds no state.</summary>
    public static FileRenameKernel Instance { get; } = new();

    /// <inheritdoc />
    public int Replace(string source, string destination)
    {
        using var handle = WindowsInterop.CreateFile(
            ExtendedPath(source),
            WindowsInterop.DeleteAccess,
            WindowsInterop.ShareReadWriteDelete,
            IntPtr.Zero,
            WindowsInterop.OpenExisting,
            flagsAndAttributes: 0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        // The name is a full path, so the root directory stays null; the buffer is never smaller
        // than the structure itself, whose trailing padding the call may read.
        var name = ExtendedPath(destination);
        var nameBytes = name.Length * sizeof(char);
        var size = Math.Max(StructureSize, NameOffset + nameBytes + sizeof(char));
        var information = Marshal.AllocHGlobal(size);
        try
        {
            for (var offset = 0; offset < size; offset++)
            {
                Marshal.WriteByte(information, offset, 0);
            }

            Marshal.WriteInt32(
                information,
                0,
                unchecked((int)(WindowsInterop.FileRenameFlagReplaceIfExists | WindowsInterop.FileRenameFlagPosixSemantics)));
            Marshal.WriteInt32(information, LengthOffset, nameBytes);
            Marshal.Copy(name.ToCharArray(), 0, information + NameOffset, name.Length);
            return WindowsInterop.SetFileInformationByHandle(handle, WindowsInterop.FileRenameInfoExClass, information, (uint)size)
                ? 0
                : Marshal.GetLastWin32Error();
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
    }

    /// <summary>
    /// The full path the kernel takes for <paramref name="path"/>, prefixed the way .NET's own
    /// <c>CreateFile</c> wrapper prefixes it: a path of <see cref="MaxShortPath"/> characters or more
    /// takes the extended-length prefix (<c>\\?\UNC\</c> for a share), which lifts the
    /// <c>MAX_PATH</c> limit without the system's long-path opt-in, and an already prefixed or
    /// device path is kept as it is. The platform replace this one stands in for prefixes the same
    /// way, so no path it accepted is refused here.
    /// </summary>
    internal static string ExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < MaxShortPath
            || full.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            || full.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(UncPrefix, StringComparison.Ordinal)
#if NET
            ? string.Concat(ExtendedUncPrefix, full.AsSpan(UncPrefix.Length))
#else
            ? ExtendedUncPrefix + full[UncPrefix.Length..]
#endif
            : ExtendedPrefix + full;
    }
}
