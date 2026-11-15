namespace OpenCode.Sdk.Internal;

/// <summary>Reads configuration settings without the CLI read's migration and repair writes.</summary>
internal sealed class ServiceConfigStore(IServiceEnvironment environment, IServiceFileSystem fileSystem)
{
    /// <summary>Reads the selected config, treating an undecodable document as empty.</summary>
    /// <param name="selection">The entry-time validated selection.</param>
    /// <param name="cancellationToken">The caller's token, forwarded unchanged to every read.</param>
    /// <returns>The decoded document, or an empty document when no config reads.</returns>
    public async Task<ServiceConfigDocument> ReadViewAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = new ServicePathResolver(environment).Resolve(selection);
        // Config operations never select direct-file mode, so they always have a config path.
        var configFile = paths.ConfigFile!;
        var bytes = await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false);
        if (bytes is not null)
        {
            return ServiceConfigReader.TryReadDocument(bytes) ?? new ServiceConfigDocument();
        }

        if (fileSystem.Exists(configFile) || paths.LegacyConfigFile is not { } donor)
        {
            return new ServiceConfigDocument();
        }

        var donorBytes = await fileSystem.TryReadAllBytesAsync(donor, cancellationToken).ConfigureAwait(false);
        var donorDocument = donorBytes is null ? null : ServiceConfigReader.TryReadDocument(donorBytes);
        return donorDocument is { LegacyRemote: null } ? donorDocument : new ServiceConfigDocument();
    }
}
