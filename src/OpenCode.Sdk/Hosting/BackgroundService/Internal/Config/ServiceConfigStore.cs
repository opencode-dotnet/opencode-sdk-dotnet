namespace OpenCode.Sdk.Internal;

/// <summary>Reads as the CLI's read does, migrating and repairing; writes as its write does.</summary>
internal sealed class ServiceConfigStore(
    IServiceEnvironment environment, IServiceFileSystem fileSystem, TimeProvider time, RandomCredential credentials)
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(10);
    private const int RenameRetries = 20;

    /// <summary>Gets the operation's injected credential generator.</summary>
    internal RandomCredential Credentials => credentials;

    /// <summary>Resolves the config operation's validated selection.</summary>
    /// <param name="selection">The entry-time snapshot.</param>
    /// <returns>The channel's paths.</returns>
    internal ServicePaths ResolvePaths(ServiceSelection selection) => new ServicePathResolver(environment).Resolve(selection);

    /// <summary>Reads, migrates and repairs, refusing settings the CLI may keep but the strict reader cannot decode.</summary>
    /// <param name="selection">The entry-time validated selection.</param>
    /// <param name="cancellationToken">The caller's token, forwarded unchanged to every read.</param>
    /// <returns>The decoded or repaired document, or empty when the CLI has no readable config.</returns>
    public async Task<ServiceConfigDocument> ReadForWriteAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = ResolvePaths(selection);
        // Config operations never select direct-file mode, so they always have a config path.
        var configFile = paths.ConfigFile!;
        var bytes = await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            if (fileSystem.Exists(configFile) || paths.LegacyConfigFile is not { } donor)
            {
                return new ServiceConfigDocument();
            }

            var donorBytes = await fileSystem.TryReadAllBytesAsync(donor, cancellationToken).ConfigureAwait(false);
            if (donorBytes is null)
            {
                return new ServiceConfigDocument();
            }

            var decodedBytes = System.Text.Encoding.UTF8.GetBytes(ServiceConfigReader.DecodeCliText(donorBytes));
            var donorDocument = ServiceConfigReader.TryReadDocument(decodedBytes);
            if (donorDocument is not { LegacyRemote: null })
            {
                if (!ServiceConfigReader.IsProvenUnreadableByCliAsCurrent(decodedBytes))
                {
                    throw Refusal(donor);
                }

                return new ServiceConfigDocument();
            }

            await new ServiceMigration(fileSystem).ApplyConfigAsync(configFile, donorBytes, cancellationToken).ConfigureAwait(false);
            bytes = await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false);
        }

        if (bytes is null)
        {
            return new ServiceConfigDocument();
        }

        var document = ServiceConfigReader.TryReadDocument(bytes);
        if (document is null)
        {
            return ServiceConfigReader.IsProvenUnreadableByCli(bytes) ? new ServiceConfigDocument() : throw Refusal(configFile);
        }

        if (document.LegacyRemote is { } enabled)
        {
            document.RepairRemote(enabled ? credentials.Route() : null);
            await WriteAsync(paths, document, cancellationToken).ConfigureAwait(false);
        }

        return document;
    }

    /// <summary>Publishes the CLI's written bytes through its overwriting PID temporary file and bounded rename retry.</summary>
    /// <param name="paths">The resolved channel paths.</param>
    /// <param name="document">The merged document.</param>
    /// <param name="cancellationToken">The caller's token, checked before every operation and forwarded unchanged.</param>
    /// <returns>The completed publication; a failed temporary file is retained.</returns>
    public async Task WriteAsync(ServicePaths paths, ServiceConfigDocument document, CancellationToken cancellationToken)
    {
        // Config operations never select direct-file mode, so ConfigFile and its directory are present.
        var configFile = paths.ConfigFile!;
        var temporary = configFile + "." + environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.CreateDirectory(Path.GetDirectoryName(configFile)!);
            cancellationToken.ThrowIfCancellationRequested();
            await fileSystem.WriteOwnerOnlyAsync(temporary, ServiceConfigJsonWriter.Write(document), cancellationToken).ConfigureAwait(false);
            await RenameAsync(temporary, configFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new OpenCodeServerException($"Could not write service config '{configFile}' through temporary file '{temporary}'.", exception);
        }
    }

    private async Task RenameAsync(string temporary, string configFile, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RenameRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                fileSystem.Rename(temporary, configFile);
                return;
            }
            catch (Exception exception) when (attempt < RenameRetries && exception is IOException or UnauthorizedAccessException)
            {
#if NET
                await Task.Delay(RetryInterval, time, cancellationToken).ConfigureAwait(false);
#else
                // Downlevel Task has no TimeProvider overload; Microsoft.Bcl.TimeProvider supplies the extension.
                await time.Delay(RetryInterval, cancellationToken).ConfigureAwait(false);
#endif
            }
        }
    }

    private static OpenCodeServerException Refusal(string path) => new(
        $"Service config '{path}' is a document opencode's CLI may read (a repeated member, an undecodable string, or an unfinished SDK parse); the SDK refuses to read or rewrite it.");
}
