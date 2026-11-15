namespace OpenCode.Sdk.Internal;

/// <summary>A compatible registered service that already owns the caller's address, in any state.</summary>
/// <param name="Registration">The registration as it was probed at the caller's URL.</param>
/// <param name="State">The state its service answered with.</param>
internal sealed record ServiceIncumbent(ServiceRegistration Registration, ServiceState State);
