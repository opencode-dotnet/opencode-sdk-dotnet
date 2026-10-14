namespace OpenCode.Sdk.Internal;

/// <summary>
/// The file access the background-service door performs, and nothing more: read, one exclusive
/// create, one rename, and one delete. The shipped implementation sits on <c>System.IO</c>; tests
/// supply an adapter over the repository's filesystem double.
/// </summary>
internal interface IServiceFileSystem
{
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
