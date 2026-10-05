namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// What a standalone start hands a <see cref="ServerChild"/>: the resolved command and its argv tail,
/// the caller's environment entries and the lease credential, the working directory, the shutdown
/// grace, the optional collector, and the two line handlers the output readers call.
/// </summary>
internal sealed record ServerChildStart
{
    /// <summary>Gets the resolved command.</summary>
    public required ResolvedExecutable Executable { get; init; }

    /// <summary>Gets the caller's leading arguments, after the executable.</summary>
    public required IReadOnlyList<string> SuppliedArguments { get; init; }

    /// <summary>Gets the launcher's own argv tail, after the supplied arguments.</summary>
    public required IReadOnlyList<string> LauncherArguments { get; init; }

    /// <summary>Gets the caller's environment entries; null adds none.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Gets the lease credential the child receives as <c>OPENCODE_PASSWORD</c>, after every other entry.</summary>
    public required string Password { get; init; }

    /// <summary>Gets the child's working directory; null keeps the caller's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the grace between the request to stop and the forced end.</summary>
    public required TimeSpan GracefulShutdownTimeout { get; init; }

    /// <summary>Gets the caller's collector, completed when the child is released; null when none was supplied.</summary>
    public OpenCodeServerOutput? Output { get; init; }

    /// <summary>Gets the handler each stdout line reaches.</summary>
    public required Action<string> OnStandardOutput { get; init; }

    /// <summary>Gets the handler each stderr line reaches.</summary>
    public required Action<string> OnStandardError { get; init; }
}
