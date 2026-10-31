using System.ComponentModel;
#if !NET
using System.Runtime.InteropServices;
#endif

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="IServiceFileSystem"/> over <c>System.IO</c>. The exclusive create mirrors
/// the pinned CLI's migration write (<c>flag: "wx", mode: 0o600</c>): on the modern targets Unix
/// gets an atomic owner-only create through <c>FileStreamOptions.UnixCreateMode</c>; on the
/// <c>netstandard2.0</c> asset the file is created first and its mode set second through the
/// polyfilled <c>File.SetUnixFileMode</c>, a <c>chmod</c> child process whose failure fails the
/// create; Windows inherits the parent directory's DACL, as Node's <c>mode</c> does there.
/// </summary>
internal sealed class ServiceFileSystem : IServiceFileSystem
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int CopyBufferSize = 4096;

    /// <summary>The POSIX-semantics rename on Windows; null elsewhere, where <c>rename(2)</c> already replaces an open file.</summary>
    private readonly IFileRenameKernel? _renameKernel;

    public ServiceFileSystem()
        : this(IsWindows() ? FileRenameKernel.Instance : null)
    {
    }

    internal ServiceFileSystem(IFileRenameKernel? renameKernel) => _renameKernel = renameKernel;

    public async Task<byte[]?> TryReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var stream = OpenRead(path);
#if NET
            await using (stream.ConfigureAwait(false))
#else
            using (stream)
#endif
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, CopyBufferSize, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Missing, under a missing directory, or unreadable: absent state, as the pinned
            // client's read folds every failure but cancellation.
            return null;
        }
    }

    /// <summary>
    /// Opens a file for reading the way libuv opens every file, sharing read, write, and delete: the
    /// daemon removes its registration on exit while clients poll it, and on Windows a reader that
    /// shared less would make the removal fail. Unix never blocks it.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The open stream.</returns>
    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, useAsync: true);

    public Task<bool> TryCreateExclusiveAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateExclusive(path, bytes));
    }

    public bool TryDelete(string path)
    {
        // File.Exists is false for a missing directory as well as a missing file, which keeps
        // Delete's DirectoryNotFoundException off this path; the pinned client's rm({force:true})
        // reads both as "nothing to remove".
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Replaces <paramref name="destination"/> with <paramref name="source"/> in one step. On Windows
    /// the replace is a POSIX-semantics rename, a recorded divergence from the pinned client's
    /// <c>node:fs</c> rename: that rename fails while any handle holds the destination, and a
    /// reader the SDK does not control (an antivirus scanner, the indexer, another client polling
    /// the sidecar) holds it with delete sharing, which the POSIX-semantics rename replaces under
    /// while the reader keeps the old contents. A system or volume that refuses that rename (Windows
    /// before 10 1607, some Windows Server 2016 installations, FAT, ReFS, some network shares) takes
    /// the platform replace below, with the error codes Git's own POSIX-semantics rename falls back on.
    /// </summary>
    /// <param name="source">The file to publish.</param>
    /// <param name="destination">The name it replaces.</param>
    /// <exception cref="IOException">The replace failed; the destination is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The replace was denied; the destination is unchanged.</exception>
    public void Rename(string source, string destination)
    {
        if (_renameKernel is { } kernel)
        {
            var error = kernel.Replace(source, destination, out var sourceOpenFailed);
            if (error is 0)
            {
                return;
            }

            if (error is not (WindowsInterop.InvalidParameter or WindowsInterop.NotSupported or WindowsInterop.InvalidFunction))
            {
                throw RenameFailure(error, source, destination, sourceOpenFailed);
            }
        }

        ReplaceByPlatform(source, destination);
    }

    /// <summary>
    /// The platform replace's exception type for the Windows error: access-denied and other I/O
    /// failures identify the failed call; not-found failures keep the platform replace's message.
    /// </summary>
    internal static Exception RenameFailure(int error, string source, string destination, bool sourceOpenFailed) =>
        error switch
        {
            WindowsInterop.FileNotFound => new FileNotFoundException($"Could not find file '{source}'.", source),
            WindowsInterop.PathNotFound => new DirectoryNotFoundException($"Could not find a part of the path '{source}'."),
            WindowsInterop.AccessDenied => new UnauthorizedAccessException($"Access to the path '{(sourceOpenFailed ? source : destination)}' is denied."),
            _ => new IOException(
                sourceOpenFailed
                    ? $"'{source}' could not be opened to replace '{destination}': {new Win32Exception(error).Message}"
                    : $"'{source}' could not replace '{destination}': {new Win32Exception(error).Message}",
                unchecked((int)0x80070000) | error),
        };

    private static void ReplaceByPlatform(string source, string destination)
    {
#if NET
        // The modern targets replace the destination in one native move (rename(2) on Unix,
        // MoveFileEx with MOVEFILE_REPLACE_EXISTING on Windows), which is the pinned client's
        // atomic sidecar publication.
        File.Move(source, destination, overwrite: true);
#else
        // net472 and netstandard2.0 have no overwrite overload. File.Replace swaps an existing
        // destination in one step (ReplaceFile on Windows, rename(2) under Mono), so no reader ever
        // sees the destination missing and a failed replace leaves it untouched; a destination that
        // does not exist yet is a plain move. A destination removed between the two looks fails the
        // replace, which the publisher reports and the election retries.
        if (File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null);
            return;
        }

        File.Move(source, destination);
#endif
    }

    private static bool IsWindows() =>
#if NET
        OperatingSystem.IsWindows();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

    /// <summary>
    /// A registration is a few hundred bytes, so the create, the mode, the write, and the close run
    /// synchronously inside one <c>using</c>: nothing interleaves with a half-written file, and the
    /// partial-file cleanup stays one catch.
    /// </summary>
    private static bool CreateExclusive(string path, ReadOnlyMemory<byte> bytes)
    {
        var created = false;
        try
        {
            using var stream = OpenExclusive(path);
            created = true;
#if !NET
            SetOwnerOnlyMode(path);
#endif
            stream.Write(bytes.Span);
            return true;
        }
        catch (IOException) when (!created && File.Exists(path))
        {
            // CreateNew refused because the target exists: the caller's "someone else wrote it
            // first" outcome, never an error, and the existing file is untouched.
            return false;
        }
        catch when (created)
        {
            // A partial file this call created must not survive as a registration candidate.
            RemovePartialFile(path);
            throw;
        }
    }

#if NET
    private static FileStream OpenExclusive(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        return new FileStream(path, options);
    }
#else
    private static FileStream OpenExclusive(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    private static void SetOwnerOnlyMode(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        File.SetUnixFileMode(path, OwnerOnly);
    }
#endif

    [SlopwatchSuppress(
        "SW003",
        "The cleanup runs inside a failed create whose own exception is rethrown: that failure is the one worth reporting, and a partial file that cannot be removed adds nothing to it.")]
    private static void RemovePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The create failed for a reason the caller already sees.
        }
    }
}
