namespace OpenCode.Sdk.Internal;

/// <summary>
/// The source open and POSIX-semantics rename the background service's Windows replace makes.
/// A test substitutes them to drive fallback and failure decisions without a file system that
/// fails on cue; access-denied and other I/O failures identify the failed call, while not-found
/// failures keep the platform replace's message.
/// </summary>
internal interface IFileRenameKernel
{
    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/>, replacing an existing
    /// destination even while handles opened with delete sharing hold it.
    /// </summary>
    /// <param name="source">The file to rename.</param>
    /// <param name="destination">The name it takes.</param>
    /// <param name="sourceOpenFailed">True when opening the source failed; false when the rename was reached.</param>
    /// <returns>Zero when the file was renamed; otherwise the Windows error code, with nothing changed.</returns>
    public int Replace(string source, string destination, out bool sourceOpenFailed);
}
