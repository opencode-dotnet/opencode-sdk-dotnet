using System.Security.Cryptography;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Reads and changes the background service's configuration managed by the CLI.</summary>
public static class OpenCodeServiceConfiguration
{
    /// <summary>
    /// The CLI's <c>opencode service get</c> with no key: reads the channel's configuration without
    /// its password or remote route, copying a legacy donor and repairing a legacy remote value
    /// as the CLI's read does, and stopping no service.
    /// </summary>
    /// <param name="options">The channel selection; null reads the shared release config.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>The configuration without the password or remote route.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback; the config or legacy donor
    /// is a document opencode's CLI may read but the SDK refuses to read; or the repair could not be written.</exception>
    /// <remarks>
    /// Copies a current-shape legacy donor only when the current file is absent. A legacy boolean
    /// <c>remote</c> is repaired in place: true becomes a new route, false drops the member.
    /// Otherwise writes nothing. A document proven unreadable by the CLI reads as empty; a document
    /// the strict reader cannot decode but the CLI may read is refused, even when no repair is needed.
    /// </remarks>
    public static async Task<OpenCodeServiceConfig> ReadAsync(
        OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        var selection = ServiceSelection.Snapshot(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var random = RandomNumberGenerator.Create();
        return await ReadSelectedAsync(selection, CreateStore(random), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The CLI's <c>opencode service get password</c>: returns the stored password, creating and storing one when absent or empty.</summary>
    /// <param name="options">The channel selection; null selects the shared release config.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates.</param>
    /// <returns>The password used when the service next starts; this is the one result that exposes the password.</returns>
    /// <exception cref="ArgumentException">An option is blank.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves; the config or legacy donor is a document
    /// opencode's CLI may read but the SDK refuses to rewrite; or the file could not be written.</exception>
    /// <remarks>Reads and repairs as every SDK-managed write does, stops nothing, and stores 32 random bytes in unpadded base64url.</remarks>
    public static async Task<string> GetOrCreatePasswordAsync(
        OpenCodeServiceConfigOptions? options = null, CancellationToken cancellationToken = default)
    {
        var selection = ServiceSelection.Snapshot(options);
        cancellationToken.ThrowIfCancellationRequested();
        using var random = RandomNumberGenerator.Create();
        return await new ServiceConfigEditor(CreateStore(random)).GetOrCreatePasswordAsync(selection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The password door over an injected editor, retaining entry-time option validation.</summary>
    /// <param name="options">The selection.</param>
    /// <param name="editor">The operation's editor.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The stored or newly persisted password.</returns>
    internal static Task<string> GetOrCreatePasswordWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceConfigEditor editor, CancellationToken cancellationToken) =>
        editor.GetOrCreatePasswordAsync(options, cancellationToken);

    private static ServiceConfigStore CreateStore(RandomNumberGenerator random)
    {
        var seams = LauncherSeams.ForCurrentProcess();
        return new ServiceConfigStore(new ServiceEnvironment(), new ServiceFileSystem(), seams.Time, new RandomCredential(random));
    }

    /// <summary>The read door over an injected store, retaining entry-time option validation.</summary>
    /// <param name="options">The channel selection.</param>
    /// <param name="store">The configuration store over the operation's environment and file access.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The safe public view.</returns>
    internal static async Task<OpenCodeServiceConfig> ReadWithSeamsAsync(
        OpenCodeServiceConfigOptions? options, ServiceConfigStore store, CancellationToken cancellationToken)
    {
        var selection = ServiceSelection.Snapshot(options);
        return await ReadSelectedAsync(selection, store, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OpenCodeServiceConfig> ReadSelectedAsync(
        ServiceSelection selection, ServiceConfigStore store, CancellationToken cancellationToken)
    {
        var document = await store.ReadForWriteAsync(selection, cancellationToken).ConfigureAwait(false);
        return OpenCodeServiceConfig.From(document);
    }
}
