namespace OpenCode.Sdk.Internal;

/// <summary>
/// The kernel call the background service's Windows replace makes: a POSIX-semantics rename over
/// an existing destination. A test replaces it to drive the replace's own decisions (a volume that
/// refuses the call, a refusal that is a failure) without a file system that fails on cue.
/// </summary>
internal interface IFileRenameKernel
{
    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/>, replacing an existing
    /// destination even while handles opened with delete sharing hold it.
    /// </summary>
    /// <param name="source">The file to rename.</param>
    /// <param name="destination">The name it takes.</param>
    /// <returns>Zero when the file was renamed; otherwise the Windows error code, with nothing changed.</returns>
    public int Replace(string source, string destination);
}
