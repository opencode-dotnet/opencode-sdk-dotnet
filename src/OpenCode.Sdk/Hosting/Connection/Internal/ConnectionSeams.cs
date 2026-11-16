namespace OpenCode.Sdk.Internal;

/// <summary>The boundaries shared by connection selection and the explicit probe.</summary>
/// <param name="Doors">Launcher and located service operations.</param>
/// <param name="Environment">The service roots.</param>
/// <param name="FileSystem">Config and registration access.</param>
/// <param name="ProbeTransport">The test-only transport, null in production.</param>
/// <param name="ProbeTime">The test-only clock, null in production.</param>
internal sealed record ConnectionSeams(
    IServerDoors Doors,
    IServiceEnvironment Environment,
    IServiceFileSystem FileSystem,
    HttpClient? ProbeTransport = null,
    TimeProvider? ProbeTime = null);
