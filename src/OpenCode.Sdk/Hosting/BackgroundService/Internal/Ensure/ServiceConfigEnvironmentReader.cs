namespace OpenCode.Sdk.Internal;

/// <summary>Reads the saved overlay without moving the service registration's state root.</summary>
internal sealed class ServiceConfigEnvironmentReader(IServiceFileSystem fileSystem)
{
    /// <summary>Captures the current config overlay, including its absence.</summary>
    /// <param name="paths">The already located service paths.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A non-null capture whose entries may be absent.</returns>
    public async Task<ServiceConfigEnvironment> ReadAsync(ServicePaths paths, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? configured = null;
        if (paths.ConfigFile is { } configFile &&
            await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false) is { } bytes)
        {
            configured = ServiceConfigReader.TryReadEnvironment(bytes)?
                .Where(static entry => !string.Equals(entry.Key, "XDG_STATE_HOME", StringComparison.Ordinal))
                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.Ordinal);
        }

        return new ServiceConfigEnvironment(configured);
    }
}
