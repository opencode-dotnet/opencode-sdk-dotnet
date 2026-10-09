namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The launch policy every owned test server shares: how long a source-run server may take to
/// report readiness on a cold runner, and the grace it gets before the forced end. The source-run
/// server needs longer than the launcher's 3-second default to leave on its own on Linux and macOS;
/// ten seconds is the policy the retired test adapter applied.
/// </summary>
internal static class OwnedServerPolicy
{
    public static TimeSpan ReadinessTimeout { get; } = TimeSpan.FromMinutes(3);

    public static TimeSpan GracefulShutdownTimeout { get; } = TimeSpan.FromSeconds(10);
}
