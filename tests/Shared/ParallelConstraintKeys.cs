namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// TUnit <c>[NotInParallel]</c> constraint keys shared by the test projects.
/// </summary>
/// <remarks>
/// Two scheduling rules hold. Every test that starts a real server
/// process — an <c>opencode serve</c> child, a drive server, a <c>bun -e</c> stand-in, or the
/// per-session pinned fixture — carries <see cref="ServerProcess"/>, so within one test host at
/// most one such test runs at a time while the in-process suite keeps running alongside; several
/// starting together stalled the hosted Windows net472 host in ten-second slices. The key is a
/// TUnit constraint, so it reaches neither the other target frameworks' hosts, which run
/// concurrently, nor a session fixture's server, which outlives the test that started it;
/// the <see cref="MachineLock.DrivePorts"/> lock is the separate cross-process lock, held only
/// while a simulated server binds its ports. Every test whose
/// assertion depends on a wall-clock bound the host can miss under load — a progress-window race,
/// a <c>WaitAsync</c> on an in-process handoff, a <c>[Timeout]</c> measured in seconds — carries
/// the keyless <c>[NotInParallel]</c> instead, which TUnit runs alone after every other test.
/// </remarks>
internal static class ParallelConstraintKeys
{
    /// <summary>The single key every server-process test shares.</summary>
    public const string ServerProcess = "server-process";
}
