namespace OpenCode.Sdk.Internal;

/// <summary>The CLI's ordered read, stop, re-read and configuration merges.</summary>
internal sealed class ServiceConfigEditor(
    ServiceConfigStore store, Func<OpenCodeServerStopOptions, CancellationToken, Task> stop)
{
    /// <summary>Changes the saved hostname after stopping the registered service.</summary>
    /// <param name="hostname">The guarded value.</param>
    /// <param name="selection">The captured selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The completed write.</returns>
    public Task SetHostnameAsync(string hostname, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document => document.AssignHostname(hostname), stopping: true, cancellationToken);

    /// <summary>Only enabling the disabled flag stops the service.</summary>
    public Task SetDisabledAsync(bool disabled, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document => document.AssignDisabled(disabled), stopping: disabled, cancellationToken);

    /// <summary>Stores the validated port after stopping.</summary>
    public Task SetPortAsync(int port, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document => document.AssignPort(port), stopping: true, cancellationToken);

    /// <summary>Stores the given password, including an empty one, after stopping.</summary>
    public Task SetPasswordAsync(string password, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document => document.AssignPassword(password), stopping: true, cancellationToken);

    /// <summary>Stores the captured origins in their given order after stopping.</summary>
    public Task SetCorsOriginsAsync(IReadOnlyList<string> origins, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document => document.AssignCorsOrigins(origins), stopping: true, cancellationToken);

    /// <summary>Updates a present environment name in place, or appends a new name.</summary>
    public Task SetEnvironmentVariableAsync(string name, string value, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document =>
        {
            var environment = (document.Environment ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            environment[name] = value;
            document.AssignEnvironment(environment);
        }, stopping: true, cancellationToken);

    /// <summary>Forgets remote access, recording acceptance only when it was enabled.</summary>
    public Task DisableRemoteAsync(ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document =>
        {
            var enabled = document.RemoteRoute is not null;
            document.RepairRemote(null);
            if (enabled)
            {
                document.AssignRemoteAccepted(true);
            }
        }, stopping: true, cancellationToken);

    /// <summary>Removes a setting; only the disabled preference is non-stopping.</summary>
    public Task UnsetAsync(OpenCodeServiceSetting setting, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document => document.RemoveSetting(setting),
            stopping: setting != OpenCodeServiceSetting.Disabled, cancellationToken);

    /// <summary>Drops env, then appends its remaining entries last when any survive.</summary>
    public Task UnsetEnvironmentVariableAsync(string name, ServiceSelection selection, CancellationToken cancellationToken) =>
        ChangeAsync(selection, document =>
        {
            var remaining = (document.Environment ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Where(pair => !string.Equals(pair.Key, name, StringComparison.Ordinal))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            document.RemoveEnvironment();
            if (remaining.Count != 0)
            {
                document.AssignEnvironment(remaining);
            }
        }, stopping: true, cancellationToken);

    private async Task ChangeAsync(
        ServiceSelection selection, Action<ServiceConfigDocument> merge, bool stopping, CancellationToken cancellationToken)
    {
        var document = await store.ReadForWriteAsync(selection, cancellationToken).ConfigureAwait(false);
        if (stopping)
        {
            await stop(new OpenCodeServerStopOptions
            {
                Channel = selection.Channel,
                InstalledVersion = selection.InstalledVersion,
            }, cancellationToken).ConfigureAwait(false);
            document = await store.ReadForWriteAsync(selection, cancellationToken).ConfigureAwait(false);
        }

        merge(document);
        await store.WriteAsync(store.ResolvePaths(selection), document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a stored nonempty password, creating and persisting one otherwise.</summary>
    /// <param name="options">The selection, validated before any seam is called.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The password, only after a required write completed.</returns>
    public Task<string> GetOrCreatePasswordAsync(OpenCodeServiceConfigOptions? options, CancellationToken cancellationToken) =>
        GetOrCreatePasswordAsync(ServiceSelection.Snapshot(options), cancellationToken);

    /// <summary>The edit over an already-snapshotted public request.</summary>
    /// <param name="selection">The validated selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The stored or newly persisted password.</returns>
    internal async Task<string> GetOrCreatePasswordAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        var document = await store.ReadForWriteAsync(selection, cancellationToken).ConfigureAwait(false);
        if (document.Password is { Length: > 0 } password)
        {
            return password;
        }

        var next = store.Credentials.Password();
        document.AssignPassword(next);
        await store.WriteAsync(store.ResolvePaths(selection), document, cancellationToken).ConfigureAwait(false);
        return next;
    }
}
