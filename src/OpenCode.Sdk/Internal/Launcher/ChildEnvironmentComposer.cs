namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Composes a server's whole environment, the way the reference client spreads
/// <c>process.env</c> under its own entries: the host's environment, then the caller's entries,
/// then the lease credential, so nothing can shadow it. Names compare the way the platform
/// compares them: case-sensitively on Linux and macOS, ignoring case on Windows, where a caller
/// entry whose name differs from a host entry only in case replaces it, as
/// <c>ProcessStartInfo.Environment</c> does. A POSIX spawn passes each entry as a C string and a
/// Windows environment block ends each entry with a NUL, so a NUL in any name or value would cut
/// it short without a word, and is refused.
/// </summary>
internal sealed class ChildEnvironmentComposer
{
    /// <summary>The variable that carries the lease credential to the server.</summary>
    public const string PasswordVariable = "OPENCODE_PASSWORD";

    private readonly IReadOnlyDictionary<string, string> _hostEnvironment;
    private readonly StringComparer _names;

    /// <summary>Initializes the composer over a snapshot of the host's environment.</summary>
    /// <param name="hostEnvironment">The host's environment.</param>
    /// <param name="names">How the platform compares variable names.</param>
    public ChildEnvironmentComposer(IReadOnlyDictionary<string, string> hostEnvironment, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(names);
        _hostEnvironment = hostEnvironment;
        _names = names;
    }

    /// <summary>Composes the environment.</summary>
    /// <param name="entries">The caller's entries; null adds none.</param>
    /// <param name="password">The lease credential.</param>
    /// <returns>The child's whole environment.</returns>
    /// <exception cref="ArgumentException">A caller entry's name or value contains NUL.</exception>
    public IReadOnlyDictionary<string, string> Compose(IReadOnlyDictionary<string, string>? entries, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var composed = new Dictionary<string, string>(_names);
        // The host's own environment cannot hold a NUL; one that somehow does is not passed on cut short.
        foreach (var entry in _hostEnvironment.Where(static entry => !NulText.Occurs(entry.Key) && !NulText.Occurs(entry.Value)))
        {
            composed[entry.Key] = entry.Value;
        }

        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (NulText.Occurs(entry.Key) || NulText.Occurs(entry.Value))
                {
                    throw new ArgumentException(
                        "OpenCodeServerOptions.Environment names and values cannot contain NUL.", nameof(entries));
                }

                composed[entry.Key] = entry.Value;
            }
        }

        composed[PasswordVariable] = password;
        return composed;
    }
}
