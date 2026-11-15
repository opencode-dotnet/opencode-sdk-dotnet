namespace OpenCode.Sdk.Internal;

/// <summary>The CLI's explicit, standalone and managed connection precedence over substituted boundaries.</summary>
internal sealed class ConnectionResolver(ConnectionSeams seams)
{
    public async Task<OpenCodeServerConnection> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Endpoint is { } endpoint)
        {
            var info = await new ExplicitServerProbe(seams.ProbeTransport, seams.ProbeTime)
                .GetInfoAsync(endpoint, request.Password, cancellationToken).ConfigureAwait(false);
            VersionMismatchNotifier.Notify(endpoint, info.Version, request.OnServerVersionMismatch);
            return new OpenCodeServerConnection(endpoint, request.Password, info.Version);
        }

        var service = EnsureRequest.Snapshot(request.Service);
        if (request.Standalone || await new ServiceDisabledCheck(seams.Environment, seams.FileSystem)
            .IsDisabledAsync(service.Selection, cancellationToken).ConfigureAwait(false))
        {
            return await StartStandaloneAsync(request.Launch, cancellationToken).ConfigureAwait(false);
        }

        var paths = await new ServiceRegistrationLocator(seams.Environment, seams.FileSystem)
            .LocateAsync(service.Selection, cancellationToken).ConfigureAwait(false);
        var overlay = await new ServiceConfigEnvironmentReader(seams.FileSystem).ReadAsync(paths, cancellationToken).ConfigureAwait(false);
        var snapshot = new ManagedServiceSnapshot(service, paths, overlay);
        if (!request.Autostart)
        {
            var discovered = await seams.Doors.DiscoverLocatedAsync(paths, expectedVersion: null, cancellationToken).ConfigureAwait(false);
            return discovered is null
                ? await StartStandaloneAsync(request.Launch, cancellationToken).ConfigureAwait(false)
                : ServiceConnection(discovered, snapshot);
        }

        var server = await seams.Doors.EnsureLocatedAsync(service, paths, overlay, cancellationToken).ConfigureAwait(false);
        return ServiceConnection(server, snapshot);
    }

    private async Task<OpenCodeServerConnection> StartStandaloneAsync(OpenCodeServerOptions? launch, CancellationToken cancellationToken)
    {
        var server = await seams.Doors.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        return new OpenCodeServerConnection(server, serverVersion: null);
    }

    private OpenCodeServerConnection ServiceConnection(OpenCodeServer server, ManagedServiceSnapshot snapshot) =>
        new(server, server.RegisteredVersion, new OpenCodeServerServiceOperations(snapshot, seams.Doors));
}
