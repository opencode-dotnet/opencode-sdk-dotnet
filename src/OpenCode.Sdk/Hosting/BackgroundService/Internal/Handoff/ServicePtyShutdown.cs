
namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="IServicePtyShutdown"/>: a client over the registration's endpoint and
/// credential, one <c>PersistentPtys.ShutdownAsync</c> call, no request bound of its own — its
/// caller, the handoff's prepare fallback, bounds the call, as the pinned client's
/// <c>prepare</c> does with <c>AbortSignal.timeout(timeout)</c> (<c>pty-handoff.ts</c>).
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
