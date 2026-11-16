using System.Security.Cryptography;

namespace OpenCode.Sdk.Internal;

/// <summary>Reads the connection-mode flag through the migrating and repairing configuration store.</summary>
internal sealed class ServiceDisabledCheck(IServiceEnvironment environment, IServiceFileSystem fileSystem, TimeProvider time)
{
    /// <summary>Checks whether the selected channel disables its managed service.</summary>
    /// <param name="selection">The validated service selection.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>True only for a valid config whose disabled flag is true.</returns>
    public async Task<bool> IsDisabledAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        using var random = RandomNumberGenerator.Create();
        var store = new ServiceConfigStore(environment, fileSystem, time, new RandomCredential(random));
        var result = await store.ReadAsync(selection, cancellationToken).ConfigureAwait(false);
        return result.Document?.Disabled == true;
    }
}
