namespace OpenCode.Sdk.Internal;

/// <summary>Forwards connection operations to the existing launcher and located service cores.</summary>
internal sealed class ServerDoors : IServerDoors
{
    public Task<OpenCodeServer> StartAsync(OpenCodeServerOptions? options, CancellationToken cancellationToken) =>
        OpenCodeServer.StartAsync(options, cancellationToken);

    public Task<OpenCodeServer?> DiscoverLocatedAsync(ServicePaths paths, string? expectedVersion, CancellationToken cancellationToken) =>
        OpenCodeServer.DiscoverLocatedAsync(paths, expectedVersion, cancellationToken);

    public Task<OpenCodeServer> EnsureLocatedAsync(
        EnsureRequest request, ServicePaths paths, ServiceConfigEnvironment snapshot, CancellationToken cancellationToken) =>
        OpenCodeServer.EnsureLocatedAsync(request, paths, snapshot, cancellationToken);

    public Task StopLocatedAsync(ServicePaths paths, OpenCodeServerStopPtyMode? mode, CancellationToken cancellationToken) =>
        OpenCodeServer.StopLocatedAsync(paths, mode, cancellationToken);
}
