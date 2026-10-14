namespace OpenCode.Sdk.Internal;

/// <summary>
/// The process environment the background-service door reads: the XDG roots, the config-root
/// override, and the user home. Supplied rather than read so the path rules are pure policy a
/// test drives on any host.
/// </summary>
internal interface IServiceEnvironment
{
    /// <summary>Reads one environment variable; null when it is not set.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The raw value, or null.</returns>
    public string? GetEnvironmentVariable(string name);

    /// <summary>
    /// Gets the user home the XDG fallbacks hang off, resolved the way libuv's <c>os.homedir()</c>
    /// resolves it (the platform variable first, the profile folder second); null when nothing
    /// resolves.
    /// </summary>
    public string? UserProfile { get; }
}
