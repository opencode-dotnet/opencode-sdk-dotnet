namespace OpenCode.Sdk.Internal;

/// <summary>The non-stopping password edit over the CLI-compatible configuration store.</summary>
internal sealed class ServiceConfigEditor(ServiceConfigStore store)
{
    /// <summary>Returns a stored nonempty password, creating and persisting one otherwise.</summary>
    /// <param name="options">The selection, validated before any seam is called.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The password, only after a required write completed.</returns>
    public Task<string> GetOrCreatePasswordAsync(OpenCodeServiceConfigOptions? options, CancellationToken cancellationToken) =>
        GetOrCreatePasswordAsync(ServiceSelection.Snapshot(options), cancellationToken);

    /// <summary>The edit over an already-snapshotted public request.</summary>
    /// <param name="selection">The validated selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The stored or newly persisted password.</returns>
    internal async Task<string> GetOrCreatePasswordAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        var document = await store.ReadForWriteAsync(selection, cancellationToken).ConfigureAwait(false);
        if (document.Password is { Length: > 0 } password)
        {
            return password;
        }

        var next = store.Credentials.Password();
        document.AssignPassword(next);
        await store.WriteAsync(store.ResolvePaths(selection), document, cancellationToken).ConfigureAwait(false);
        return next;
    }
}
