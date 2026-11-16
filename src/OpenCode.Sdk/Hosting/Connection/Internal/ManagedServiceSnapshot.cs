namespace OpenCode.Sdk.Internal;

/// <summary>The request, paths and overlay captured once when a managed connection resolves.</summary>
/// <param name="Request">The validated resolve-time service request.</param>
/// <param name="Paths">The located paths.</param>
/// <param name="Overlay">The captured overlay, including captured absence.</param>
internal sealed record ManagedServiceSnapshot(EnsureRequest Request, ServicePaths Paths, ServiceConfigEnvironment Overlay)
{
    /// <summary>Gets the same inputs without version replacement or a start announcement.</summary>
    public EnsureRequest ReconnectRequest => Request with { Policy = OpenCodeServerVersionPolicy.Ignore, OnStart = null };
}
