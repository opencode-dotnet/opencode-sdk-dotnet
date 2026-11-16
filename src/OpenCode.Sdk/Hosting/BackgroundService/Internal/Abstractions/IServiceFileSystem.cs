namespace OpenCode.Sdk.Internal;

/// <summary>
/// The file access the background-service door performs, and nothing more: read, one exclusive
/// create, existence, directory creation, an overwriting write, rename, and delete. The shipped implementation sits on <c>System.IO</c>; tests
/// supply an adapter over the repository's filesystem double.
/// </summary>
internal interface IServiceFileSystem
{
    /// <summary>Creates a directory and its parents; an existing directory is success.</summary>
    /// <param name="path">The directory path.</param>
    public void CreateDirectory(string path);

    /// <summary>Creates owner-only where supported, or truncates an existing file without changing its access control.</summary>
    /// <param name="path">The temporary file.</param>
    /// <param name="bytes">The complete content.</param>
    /// <param name="cancellationToken">Checked before starting; a started write completes and leaves partial output on failure.</param>
    /// <returns>The completed write.</returns>
    public Task WriteOwnerOnlyAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Whether any directory entry, including a directory or link, occupies the path.</summary>
    /// <param name="path">The absolute path an exclusive create would find occupied.</param>
    /// <returns>True when an entry exists or the path cannot be inspected.</returns>
    public bool Exists(string path);

    /// <summary>
    /// Reads a whole file the way the pinned client's <c>read</c> treats one: a file that is
    /// missing, sits under a missing directory, or cannot be read is absent state, never a
    /// failure. Cancellation is the one failure that propagates.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The file's bytes, or null when there is no readable file.</returns>
    public Task<byte[]?> TryReadAllBytesAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a file that must not exist yet, with owner-only access where the platform can
    /// express it, and writes the bytes. An existing target is reported, never replaced; every
    /// other failure throws and leaves no partial file behind.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="bytes">The content.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True when the file was created; false when the target already existed.</returns>
    public Task<bool> TryCreateExclusiveAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a file onto another path in the same directory, replacing an existing destination
    /// atomically where the platform can express it. The pinned client's sidecar publication
    /// (<c>rename</c> in <c>pty-handoff.ts</c> <c>publish</c>) depends on replace-on-success semantics.
    /// </summary>
    /// <param name="source">The current path.</param>
    /// <param name="destination">The destination path.</param>
    public void Rename(string source, string destination);

    /// <summary>
    /// Deletes a file the way <c>rm -f</c> does: a missing file, or a missing directory above it,
    /// is the false outcome rather than an error; a file that exists but cannot be removed throws.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>True when a file was removed; false when there was nothing to remove.</returns>
    public bool TryDelete(string path);
}
