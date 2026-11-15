namespace OpenCode.Sdk.Internal;

/// <summary>Reads the connection-mode flag after the config-only migration.</summary>
internal sealed class ServiceDisabledCheck(IServiceEnvironment environment, IServiceFileSystem fileSystem)
{
    /// <summary>Checks whether the selected channel disables its managed service.</summary>
    /// <param name="selection">The validated service selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True only for a valid config whose disabled flag is true.</returns>
    public async Task<bool> IsDisabledAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        var paths = new ServicePathResolver(environment).Resolve(selection);
        await new ServiceMigration(fileSystem).ApplyConfigAsync(paths, cancellationToken).ConfigureAwait(false);
        if (paths.ConfigFile is not { } configFile)
        {
            return false;
        }

        var bytes = await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false);
        return bytes is not null && ServiceConfigReader.TryReadDisabled(bytes) == true;
    }
}
