namespace OpenCode.Sdk.Internal;

/// <summary>The validated, owned snapshot of one remote-on invocation, before resolution or I/O.</summary>
internal sealed class ServiceRemoteRequest(
    ServiceSelection selection, IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment)
{
    public ServiceSelection Selection { get; } = selection;
    public IReadOnlyList<string> Command { get; } = command;
    public IReadOnlyDictionary<string, string>? Environment { get; } = environment;

    /// <summary>Captures and validates the selection, command and overlay with Ensure's acceptance rules.</summary>
    /// <param name="options">The caller's options, or the defaults.</param>
    /// <returns>The snapshot; no collection remains caller-owned.</returns>
    internal static ServiceRemoteRequest Snapshot(OpenCodeServiceConfigOptions? options)
    {
        var selection = ServiceSelection.Snapshot(options);
        options ??= new OpenCodeServiceConfigOptions();
        var command = options.CliCommand?.ToArray();
        if (command is not { Length: > 0 } ||
            Array.Exists(command, static entry => string.IsNullOrWhiteSpace(entry) || NulText.Occurs(entry)))
        {
            throw new ArgumentException(
                "OpenCodeServiceConfigOptions.CliCommand needs an executable and nonblank entries without NUL.", nameof(options));
        }

        var environment = options.Environment?.ToArray();
        if (environment is not null && Array.Exists(environment, static entry =>
                string.IsNullOrWhiteSpace(entry.Key) || NulText.Occurs(entry.Key) ||
                entry.Value is null || NulText.Occurs(entry.Value)))
        {
            throw new ArgumentException(
                "OpenCodeServiceConfigOptions.Environment keys cannot be blank, values cannot be null, and neither can contain NUL.",
                nameof(options));
        }

        return new ServiceRemoteRequest(selection, command,
            environment?.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal));
    }
}
