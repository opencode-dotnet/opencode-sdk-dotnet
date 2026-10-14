
namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="IServicePtyShutdown"/>: a client over the registration's endpoint and
/// credential, one <c>PersistentPtys.ShutdownAsync</c> call, no request bound of its own — the
/// pinned CLI's <c>shutdownPersistentPty</c> sets none either (<c>server-connection.ts</c>).
/// </summary>
internal sealed class ServicePtyShutdown : IServicePtyShutdown
{
    public async Task ShutdownAsync(ServiceRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);

        using var client = new OpenCodeClient(new OpenCodeClientOptions
        {
            Endpoint = registration.Endpoint,
            Password = registration.Password,
        });
        _ = await client.PersistentPtys.ShutdownAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
