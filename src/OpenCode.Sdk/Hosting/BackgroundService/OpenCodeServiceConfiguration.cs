using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Reads and administers the background service's configuration managed by the CLI.</summary>
public static class OpenCodeServiceConfiguration
{
    /// <summary>
    /// The CLI's <c>opencode service get</c> with no key: reads the channel's configuration without
    /// its password, writing nothing, creating no file or directory, and stopping no service.
    /// </summary>
    /// <param name="options">The channel selection; null reads the shared release config.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>The configuration without the password or remote route.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback.</exception>
    /// <remarks>
    /// Reads the legacy donor only when the current file is absent, without copying it. A legacy
    /// boolean <c>remote</c> reads as the setting the CLI's repair produces, without repairing the
    /// file. A document the SDK cannot decode reads as an empty config.
    /// </remarks>
    public static Task<OpenCodeServiceConfig> ReadAsync(
        OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default) =>
        ReadWithSeamsAsync(options, new ServiceConfigStore(new ServiceEnvironment(), new ServiceFileSystem()), cancellationToken);

    /// <summary>The read door over an injected store, retaining entry-time option validation.</summary>
    /// <param name="options">The channel selection.</param>
    /// <param name="store">The configuration store over the operation's environment and file access.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The safe public view.</returns>
    internal static async Task<OpenCodeServiceConfig> ReadWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceConfigStore store, CancellationToken cancellationToken)
    {
        var selection = ServiceSelection.Snapshot(options);
        var document = await store.ReadViewAsync(selection, cancellationToken).ConfigureAwait(false);
        return OpenCodeServiceConfig.From(document);
    }
}
