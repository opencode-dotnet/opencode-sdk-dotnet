namespace OpenCode.Sdk.Internal;

/// <summary>The process and network boundaries the connection composition calls.</summary>
internal interface IServerDoors
{
    public Task<OpenCodeServer> StartAsync(OpenCodeServerOptions? options, CancellationToken cancellationToken);

    public Task<OpenCodeServer?> DiscoverLocatedAsync(ServicePaths paths, string? expectedVersion, CancellationToken cancellationToken);

    public Task<OpenCodeServer> EnsureLocatedAsync(
        EnsureRequest request, ServicePaths paths, ServiceConfigEnvironment snapshot, CancellationToken cancellationToken);

    public Task StopLocatedAsync(ServicePaths paths, OpenCodeServerStopPtyMode? mode, CancellationToken cancellationToken);
}
