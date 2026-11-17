namespace OpenCode.Sdk.Internal;

/// <summary>Hands remote-on to the CLI, then obtains its route through the configuration store's shared read.</summary>
internal sealed class ServiceRemoteAccess(IServiceCliRunner runner, ExecutableResolver resolver, ServiceConfigStore store)
{
    /// <summary>Runs the snapshotted CLI before any SDK configuration read.</summary>
    /// <param name="options">The command, overlay and read-back selection, which must name the same config.</param>
    /// <param name="cancellationToken">The caller's token, forwarded unchanged.</param>
    /// <returns>The route, a credential the caller must keep private.</returns>
    public async Task<string> TurnOnAsync(OpenCodeServiceConfigOptions? options, CancellationToken cancellationToken)
    {
        var request = ServiceRemoteRequest.Snapshot(options);
        var executable = resolver.Resolve(request.Command[0]);
        await runner.RunAsync(executable, [.. request.Command.Skip(1), "service", "set", "remote", "true"],
            request.Environment, cancellationToken).ConfigureAwait(false);
        return await store.ReadRouteAsync(request.Selection, cancellationToken).ConfigureAwait(false);
    }
}
