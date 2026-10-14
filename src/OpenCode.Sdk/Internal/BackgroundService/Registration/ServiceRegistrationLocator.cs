namespace OpenCode.Sdk.Internal;

/// <summary>
/// Where a selection's registration lives, the first step Discover, Stop, and Ensure share: resolve
/// the channel's paths from the environment roots, then run the channel's legacy migration the way
/// the CLI's <c>ServiceConfig.options()</c> runs it on every connection (<c>service-config.ts</c>).
/// Reading the file stays with <see cref="ServiceRegistrationReader"/>.
/// </summary>
internal sealed class ServiceRegistrationLocator(IServiceEnvironment environment, IServiceFileSystem fileSystem)
{
    /// <summary>Resolves and migrates the selection's paths.</summary>
    /// <param name="selection">The validated selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The paths; the registration file among them is the one to read.</returns>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback.</exception>
    public async Task<ServicePaths> LocateAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        var paths = new ServicePathResolver(environment).Resolve(selection);
        await new ServiceMigration(fileSystem).ApplyAsync(selection, paths, cancellationToken).ConfigureAwait(false);
        return paths;
    }
}
