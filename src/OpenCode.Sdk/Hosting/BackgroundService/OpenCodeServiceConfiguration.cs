using System.Security.Cryptography;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Reads and changes the background service's configuration managed by the CLI.</summary>
public static class OpenCodeServiceConfiguration
{
    /// <summary>Turns remote access on through the installed CLI's <c>opencode service set remote true</c> and returns its secret route.</summary>
    /// <param name="options">The CLI command, environment and read-back channel; null uses the defaults.</param>
    /// <param name="cancellationToken">The caller's token, observed during the CLI run and subsequent config read.</param>
    /// <returns>The secret part of the remote address, read from the channel's config after CLI success. Treat it as a credential;
    /// the CLI never prints it.</returns>
    /// <exception cref="ArgumentException">The channel or version is blank, or the command or environment is invalid.</exception>
    /// <exception cref="OpenCodeServerException">The command does not resolve or cannot run; the CLI ends unsuccessfully;
    /// no user home resolves; a config repair fails; or CLI success leaves no route the SDK can read in the selected config.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled. Cancellation after CLI success may leave remote access enabled.</exception>
    /// <remarks>
    /// The CLI's own OpenTunnel client creates or resumes the device's tunnel, which can take up to
    /// five minutes, then stops its channel's registered service and stores the route. Nothing restarts it.
    /// The SDK reads no config before the run and performs no stop or write for the CLI. Its refusal
    /// protects only SDK-managed writes: the CLI reads and rewrites what upstream accepts, including
    /// documents the SDK's writes refuse, without losing settings it can read.
    /// CliCommand, Channel and Environment must describe the same config. The read-back uses the
    /// SDK process's roots and the shared read, which may migrate a donor or repair a legacy remote value.
    /// A failure message includes the CLI's bounded stderr tail, external text that can contain
    /// whatever the CLI printed, including secrets.
    /// </remarks>
    public static async Task<string> EnableRemoteAsync(
        OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        var seams = LauncherSeams.ForCurrentProcess();
        return await EnableRemoteWithSeamsAsync(options, new ServiceRemoteAccess(
            new ServiceCliRunner(seams), new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()),
            CreateStore(random, seams, out _)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The remote-on door over its injected CLI, resolver and shared configuration store.</summary>
    internal static Task<string> EnableRemoteWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceRemoteAccess remote, CancellationToken cancellationToken) =>
        remote.TurnOnAsync(options, cancellationToken);

    /// <summary>
    /// The CLI's <c>opencode service get</c> with no key: reads the channel's configuration without
    /// its password or remote route, copying a legacy donor and repairing a legacy remote value
    /// as the CLI's read does, and stopping no service.
    /// </summary>
    /// <param name="options">The channel selection; null reads the shared release config.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>The configuration without the password or remote route.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback; the config or legacy donor
    /// is a document opencode's CLI may read but the SDK refuses to read; or the repair could not be written.</exception>
    /// <remarks>
    /// Copies a current-shape legacy donor only when the current file is absent. A legacy boolean
    /// <c>remote</c> is repaired in place: true becomes a new route, false drops the member.
    /// Otherwise writes nothing. A document proven unreadable by the CLI reads as empty; a document
    /// the strict reader cannot decode but the CLI may read is refused, even when no repair is needed.
    /// </remarks>
    public static async Task<OpenCodeServiceConfig> ReadAsync(
        OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        return await ReadWithSeamsAsync(options, CreateStore(random, out _), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The CLI's <c>opencode service get password</c>: returns the stored password, creating and storing one when absent or empty.</summary>
    /// <param name="options">The channel selection; null selects the shared release config.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>The password used when the service next starts; this is the one result that exposes the password.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves; the config or legacy donor is a document
    /// opencode's CLI may read but the SDK refuses to rewrite; or the file could not be written.</exception>
    /// <remarks>Reads and repairs as every SDK-managed write does, stops nothing, and stores 32 random bytes in unpadded base64url.</remarks>
    public static async Task<string> GetOrCreatePasswordAsync(
        OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        return await GetOrCreatePasswordWithSeamsAsync(
            options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The CLI's <c>opencode service set hostname</c>; saves even an empty hostname.</summary>
    /// <param name="hostname">The hostname for the service's next start.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hostname"/> is null.</exception>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel, then rewrites the whole file the CLI's way.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task SetHostnameAsync(
        string hostname, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await SetHostnameWithSeamsAsync(hostname, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The hostname door over the same guarded path used by its public composition.</summary>
    internal static Task SetHostnameWithSeamsAsync(
        string hostname, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostname);
        return editor.SetHostnameAsync(hostname, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The CLI's <c>opencode service set disabled</c>; only true stops the registered service.</summary>
    /// <param name="disabled">The saved disabled preference.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>When true, stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel. False stops nothing.
    /// Rewrites the whole file the CLI's way, restarts nothing, and leaves the requested change unmade if stopping fails.</remarks>
    public static async Task SetDisabledAsync(
        bool disabled, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await SetDisabledWithSeamsAsync(disabled, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The disabled door's shared snapshot and forwarding path.</summary>
    internal static Task SetDisabledWithSeamsAsync(
        bool disabled, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken) =>
        editor.SetDisabledAsync(disabled, ServiceSelection.Snapshot(options), cancellationToken);

    /// <summary>The CLI's <c>opencode service set port</c>.</summary>
    /// <param name="port">The listen port, from 1 to 65535.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The port is outside 1 to 65535.</exception>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel, then rewrites the whole file the CLI's way.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task SetPortAsync(int port, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await SetPortWithSeamsAsync(port, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The port door's shared range guard and snapshot.</summary>
    internal static Task SetPortWithSeamsAsync(
        int port, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return editor.SetPortAsync(port, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The CLI's <c>opencode service set password</c>; stores the Basic password of the next start, even an empty string.</summary>
    /// <param name="password">The password stored as given.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentNullException">The password is null.</exception>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel, then rewrites the whole file the CLI's way.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task SetPasswordAsync(
        string password, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await SetPasswordWithSeamsAsync(password, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The password setter's shared guard and snapshot.</summary>
    internal static Task SetPasswordWithSeamsAsync(
        string password, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);
        return editor.SetPasswordAsync(password, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The CLI's <c>opencode service set cors</c>; keeps origin order and duplicates after the CLI's trimming.</summary>
    /// <param name="origins">A nonempty list of exact HTTP(S) origins without commas.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentNullException">The list or one item is null.</exception>
    /// <exception cref="ArgumentException">The list is empty, an item is invalid, or an option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel, then rewrites the whole file the CLI's way.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task SetCorsOriginsAsync(
        IReadOnlyList<string> origins, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await SetCorsOriginsWithSeamsAsync(origins, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The CORS door captures and validates each item before reading or stopping.</summary>
    internal static Task SetCorsOriginsWithSeamsAsync(
        IReadOnlyList<string> origins, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origins);
        var snapshot = origins.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Origins must contain at least one HTTP(S) origin.", nameof(origins));
        }
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index] is null)
            {
                throw new ArgumentNullException(nameof(origins));
            }
            var item = CorsOriginPolicy.TrimItem(snapshot[index]);
            if (item.Contains(',', StringComparison.Ordinal) || !CorsOriginPolicy.IsOrigin(item))
            {
                throw new ArgumentException($"Invalid CORS origin '{item}'.", nameof(origins));
            }
            snapshot[index] = item;
        }
        return editor.SetCorsOriginsAsync(snapshot, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The CLI's <c>opencode service set env</c>; empty names and values are stored as given.</summary>
    /// <param name="name">The environment name.</param>
    /// <param name="value">The saved value.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentNullException">The name or value is null.</exception>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel, then rewrites the whole file the CLI's way.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task SetEnvironmentVariableAsync(
        string name, string value, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await SetEnvironmentVariableWithSeamsAsync(name, value, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The environment setter's shared guards and snapshot.</summary>
    internal static Task SetEnvironmentVariableWithSeamsAsync(
        string name, string value, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        return editor.SetEnvironmentVariableAsync(name, value, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The CLI's <c>opencode service set remote false</c> and <c>opencode service unset remote</c>.</summary>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel and rewrites the whole file the CLI's way.
    /// Forgets the route: enabling again issues a new address. Records acceptance when remote was enabled, leaving it unchanged otherwise.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task DisableRemoteAsync(OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await DisableRemoteWithSeamsAsync(options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The remote-off door's shared snapshot and forwarding path.</summary>
    internal static Task DisableRemoteWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken) =>
        editor.DisableRemoteAsync(ServiceSelection.Snapshot(options), cancellationToken);

    /// <summary>The CLI's <c>opencode service unset</c> for a typed setting.</summary>
    /// <param name="setting">The setting to remove; use the dedicated methods for remote and environment.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The setting is undefined.</exception>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Except for Disabled, stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel.
    /// Rewrites the whole file the CLI's way, restarts nothing, and leaves the requested change unmade if stopping fails.</remarks>
    public static async Task UnsetAsync(
        OpenCodeServiceSetting setting, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await UnsetWithSeamsAsync(setting, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The unset door's shared enum guard and snapshot.</summary>
    internal static Task UnsetWithSeamsAsync(
        OpenCodeServiceSetting setting, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        if (setting is < OpenCodeServiceSetting.Disabled or > OpenCodeServiceSetting.CorsOrigins)
        {
            throw new ArgumentOutOfRangeException(nameof(setting), setting, "The service setting is undefined.");
        }
        return editor.UnsetAsync(setting, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The CLI's <c>opencode service unset env</c>; removes the map when its last entry is removed.</summary>
    /// <param name="name">The name to remove; an empty name is a value.</param>
    /// <param name="options">The channel selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed configuration change.</returns>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No home resolves; the CLI-readable config or donor is refused; stopping or writing fails.</exception>
    /// <remarks>Stops as <see cref="OpenCodeServer.StopAsync"/> does for this channel, then rewrites the whole file the CLI's way.
    /// Nothing restarts the service. A failed stop leaves the requested change unmade.</remarks>
    public static async Task UnsetEnvironmentVariableAsync(
        string name, OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var random = RandomNumberGenerator.Create();
        await UnsetEnvironmentVariableWithSeamsAsync(name, options, CreateEditor(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The environment unsetter's shared guard and snapshot.</summary>
    internal static Task UnsetEnvironmentVariableWithSeamsAsync(
        string name, OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return editor.UnsetEnvironmentVariableAsync(name, ServiceSelection.Snapshot(options), cancellationToken);
    }

    /// <summary>The password door over an injected editor, retaining entry-time option validation.</summary>
    /// <param name="options">The selection.</param>
    /// <param name="editor">The operation's editor.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The stored or newly persisted password.</returns>
    internal static Task<string> GetOrCreatePasswordWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken) =>
        editor.GetOrCreatePasswordAsync(options, cancellationToken);

    private static ServiceConfigEditor CreateEditor(RandomNumberGenerator random)
    {
        var store = CreateStore(random, out var stop);
        return new ServiceConfigEditor(store, stop);
    }

    private static ServiceConfigStore CreateStore(
        RandomNumberGenerator random, out Func<OpenCodeServerStopOptions, CancellationToken, Task> stop) =>
        CreateStore(random, LauncherSeams.ForCurrentProcess(), out stop);

    private static ServiceConfigStore CreateStore(
        RandomNumberGenerator random, LauncherSeams seams, out Func<OpenCodeServerStopOptions, CancellationToken, Task> stop)
    {
        var environment = new ServiceEnvironment();
        var fileSystem = new ServiceFileSystem();
        stop = (options, token) => OpenCodeServer.SystemStopper(environment, fileSystem, seams.Time).StopAsync(options, token);
        return new ServiceConfigStore(environment, fileSystem, seams.Time, new RandomCredential(random));
    }

    /// <summary>The read door over an injected store, retaining entry-time option validation.</summary>
    /// <param name="options">The channel selection.</param>
    /// <param name="store">The configuration store over the operation's environment and file access.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The safe public view.</returns>
    internal static async Task<OpenCodeServiceConfig> ReadWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceConfigStore store, CancellationToken cancellationToken)
    {
        var selection = ServiceSelection.Snapshot(options);
        return await ReadSelectedAsync(selection, store, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OpenCodeServiceConfig> ReadSelectedAsync(
        ServiceSelection selection, ServiceConfigStore store, CancellationToken cancellationToken)
    {
        var document = await store.ReadForWriteAsync(selection, cancellationToken).ConfigureAwait(false);
        return OpenCodeServiceConfig.From(document);
    }
}
