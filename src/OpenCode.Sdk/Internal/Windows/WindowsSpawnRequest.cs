namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// One Windows spawn: the executable <c>CreateProcessW</c> starts, the whole command line it gets,
/// its environment and directory, where its standard handles lead, and the two placements a caller
/// can ask for. The environment is passed as it is, so the caller owns the merge and the refusal of
/// NUL, which the environment block would silently cut.
/// </summary>
internal sealed record WindowsSpawnRequest
{
    /// <summary>
    /// Gets the executable: a resolved path, so no second search runs and a path with spaces needs
    /// no quoting; null leaves <c>CreateProcessW</c> to find it from the command line's first token.
    /// </summary>
    public required string? ApplicationPath { get; init; }

    /// <summary>Gets the whole command line, <c>argv[0]</c> included, as the child's runtime will parse it.</summary>
    public required string CommandLine { get; init; }

    /// <summary>Gets the child's complete environment; null hands it the host's own.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Gets the directory the child starts in; null keeps the host's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets where the standard handles lead; null passes the child no handle at all, so it inherits
    /// nothing and its standard handles are those of the console it gets.
    /// </summary>
    public WindowsStandardStreams? StandardStreams { get; init; }

    /// <summary>Gets a value indicating whether the child is created suspended, its main thread returned for the caller to resume.</summary>
    public bool StartSuspended { get; init; }

    /// <summary>Gets a value indicating whether the child is detached: no console, and a process group of its own.</summary>
    public bool Detached { get; init; }
}
