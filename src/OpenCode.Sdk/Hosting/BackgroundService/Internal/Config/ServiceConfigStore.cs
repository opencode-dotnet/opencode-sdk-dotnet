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

    /// <summary>Obtains the route after CLI success through the same migrating and repairing read as other store consumers.</summary>
    /// <param name="selection">The entry-time validated selection, resolved under the SDK process environment.</param>
    /// <param name="cancellationToken">The caller's token, forwarded unchanged.</param>
    /// <returns>The readable route; a strict-reader refusal is unavailable, not a write refusal.</returns>
    public async Task<string> ReadRouteAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(selection, cancellationToken).ConfigureAwait(false);
        return result.Document?.RemoteRoute ?? throw new OpenCodeServerException(
            $"The CLI reported success, but service config '{ResolvePaths(selection).ConfigFile}' holds no remote route the SDK can read. " +
            "CliCommand, Channel and Environment may name different configs.");
    }

    /// <summary>Reads, migrates and repairs, refusing settings the CLI may keep but the strict reader cannot decode.</summary>
    /// <param name="selection">The entry-time validated selection.</param>
    /// <param name="cancellationToken">The caller's token, forwarded unchanged to every read.</param>
    /// <returns>The decoded or repaired document, or empty when the CLI has no readable config.</returns>
    public async Task<ServiceConfigDocument> ReadForWriteAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        var result = await ReadAsync(selection, cancellationToken).ConfigureAwait(false);
        return result.RefusedPath is { } path ? throw Refusal(path) : result.Document ?? new ServiceConfigDocument();
    }

    /// <summary>Reads, migrates and repairs, distinguishing absence from a strict-reader refusal.</summary>
    /// <param name="selection">The entry-time validated selection.</param>
    /// <param name="cancellationToken">The caller's token, forwarded unchanged to every read.</param>
    /// <returns>The document, a refused path, or absence when the CLI has no readable config.</returns>
    internal async Task<ServiceConfigReadResult> ReadAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = ResolvePaths(selection);
        if (paths.ConfigFile is not { } configFile)
        {
            return ServiceConfigReadResult.Absent;
        }

        var bytes = await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            if (fileSystem.Exists(configFile) || paths.LegacyConfigFile is not { } donor)
            {
                return ServiceConfigReadResult.Absent;
            }

            var donorBytes = await fileSystem.TryReadAllBytesAsync(donor, cancellationToken).ConfigureAwait(false);
            if (donorBytes is null)
            {
                return ServiceConfigReadResult.Absent;
            }

            var decodedBytes = ServiceConfigReader.DecodeCliDonor(donorBytes);
            if (decodedBytes is null)
            {
                return ServiceConfigReadResult.Absent;
            }

            var donorDocument = ServiceConfigReader.TryReadDocument(decodedBytes);
            if (donorDocument is not { LegacyRemote: null })
            {
                if (!ServiceConfigReader.IsProvenUnreadableByCliAsCurrent(decodedBytes))
                {
                    return new ServiceConfigReadResult(Document: null, RefusedPath: donor);
                }

                return ServiceConfigReadResult.Absent;
            }

            await new ServiceMigration(fileSystem).ApplyConfigAsync(configFile, decodedBytes, cancellationToken).ConfigureAwait(false);
            bytes = await fileSystem.TryReadAllBytesAsync(configFile, cancellationToken).ConfigureAwait(false);
        }

        if (bytes is null)
        {
            return ServiceConfigReadResult.Absent;
        }

        var document = ServiceConfigReader.TryReadDocument(bytes);
        if (document is null)
        {
            return ServiceConfigReader.IsProvenUnreadableByCli(bytes)
                ? ServiceConfigReadResult.Absent : new ServiceConfigReadResult(Document: null, RefusedPath: configFile);
        }

        if (document.LegacyRemote is { } enabled)
        {
            document.RepairRemote(enabled ? credentials.Route() : null);
            await WriteAsync(paths, document, cancellationToken).ConfigureAwait(false);
        }

        return new ServiceConfigReadResult(document, RefusedPath: null);
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
