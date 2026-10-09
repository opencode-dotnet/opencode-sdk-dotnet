using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
namespace OpenCode.Sdk.Internal.BackgroundService.Registration;

/// <summary>
/// The pinned client's <c>same()</c> (<c>service-probe.ts</c>): two registrations name the same
/// service when their <c>id</c>, <c>version</c>, <c>url</c>, and <c>pid</c> agree. The password is
/// not part of it, and neither is any operating-system token — the process behind the pid is
/// identified separately by <see cref="ProcessIdentity"/>.
/// </summary>
/// <param name="Id">The daemon's instance id, when it published one.</param>
/// <param name="Version">The version the daemon published, when it published one.</param>
/// <param name="Url">The raw URL string exactly as written.</param>
/// <param name="ProcessId">The pid the registration names.</param>
internal readonly record struct ServiceRegistrationIdentity(string? Id, string? Version, string Url, int ProcessId)
{
    /// <summary>Takes the identity fields of a decoded registration.</summary>
    /// <param name="registration">The registration.</param>
    /// <returns>Its identity.</returns>
    public static ServiceRegistrationIdentity Of(ServiceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new ServiceRegistrationIdentity(registration.Id, registration.Version, registration.Url, registration.ProcessId);
    }
}
