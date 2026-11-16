using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

public partial class OpenCodeServer
{
    /// <summary>Resolves an explicit endpoint, an owned standalone server, or the shared managed service, in that order.</summary>
    /// <param name="options">Connection selection; null ensures the default managed service unless its config disables it.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation is never wrapped.</param>
    /// <returns>A connection whose mode describes ownership and available service operations.</returns>
    /// <exception cref="ArgumentException">Options contradict one another, the endpoint is refused, or managed options are invalid.</exception>
    /// <exception cref="OpenCodeServerException">An explicit probe failed or a launcher or service door failed.</exception>
    public static async Task<OpenCodeServerConnection> ResolveAsync(
        OpenCodeServerResolveOptions? options = null, CancellationToken cancellationToken = default) =>
        await ResolveWithSeamsAsync(options, new ConnectionSeams(new ServerDoors(), new ServiceEnvironment(), new ServiceFileSystem()), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The same resolver over injected external boundaries.</summary>
    internal static Task<OpenCodeServerConnection> ResolveWithSeamsAsync(
        OpenCodeServerResolveOptions? options, ConnectionSeams seams, CancellationToken cancellationToken) =>
        new ConnectionResolver(seams).ResolveAsync(ResolveRequest.Snapshot(options), cancellationToken);
}
