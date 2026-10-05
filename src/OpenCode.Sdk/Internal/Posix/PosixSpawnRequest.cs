namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// One POSIX spawn: what to run, with which argv and environment, and where each standard
/// descriptor leads. The environment is the child's whole environment, already merged; it is
/// passed as is, so the caller owns the merge and the refusal of NUL, which the native string
/// marshalling would silently cut.
/// </summary>
internal sealed record PosixSpawnRequest
{
    /// <summary>Gets the executable; it also becomes <c>argv[0]</c>. A name without a slash is searched on the child environment's <c>PATH</c>, as <c>posix_spawnp</c> does.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Gets the arguments after <c>argv[0]</c>, in order.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Gets the child's complete environment.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>Gets where descriptor 0 leads.</summary>
    public required ChildStreamRoute StandardInput { get; init; }

    /// <summary>Gets where descriptor 1 leads.</summary>
    public required ChildStreamRoute StandardOutput { get; init; }

    /// <summary>Gets where descriptor 2 leads.</summary>
    public required ChildStreamRoute StandardError { get; init; }
}
