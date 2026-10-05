namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Composes a POSIX server's whole environment, the way the reference client spreads
/// <c>process.env</c> under its own entries: the host's environment, then the caller's entries,
/// then the lease credential, so nothing can shadow it. Names compare case-sensitively, as they do
/// on Linux and macOS. The spawn passes each entry as a C string, which a NUL would cut short
/// without a word, so a NUL in any name or value is refused.
/// </summary>
internal sealed class ChildEnvironmentComposer
{
    /// <summary>The variable that carries the lease credential to the server.</summary>
    public const string PasswordVariable = "OPENCODE_PASSWORD";

    private readonly IReadOnlyDictionary<string, string> _hostEnvironment;

    /// <summary>Initializes the composer over a snapshot of the host's environment.</summary>
    /// <param name="hostEnvironment">The host's environment.</param>
    public ChildEnvironmentComposer(IReadOnlyDictionary<string, string> hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        _hostEnvironment = hostEnvironment;
    }

    /// <summary>Composes the environment.</summary>
    /// <param name="entries">The caller's entries; null adds none.</param>
    /// <param name="password">The lease credential.</param>
    /// <returns>The child's whole environment.</returns>
    /// <exception cref="ArgumentException">A caller entry's name or value contains NUL.</exception>
    public IReadOnlyDictionary<string, string> Compose(IReadOnlyDictionary<string, string>? entries, string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var composed = new Dictionary<string, string>(StringComparer.Ordinal);
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
