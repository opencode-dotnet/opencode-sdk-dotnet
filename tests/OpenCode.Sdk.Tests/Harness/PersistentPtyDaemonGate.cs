namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Whether persistentPty tests should expect the <c>opencode-pty</c> daemon to be reachable.
/// </summary>
/// <remarks>
/// The daemon ships as the npm package <c>@opencode-ai/pty</c> with darwin/linux platform
/// binaries only - no win32 package exists at the pinned upstream commit - so a live test on
/// Windows must expect every route to take its daemon-absent arm (<c>create</c> returns 503;
/// the others take their existence-independent arms). <c>OPENCODE_SDK_TESTS_PTY_DAEMON</c>
/// overrides the platform default, which is what lets a Windows workstation running the live leg
/// against a WSL2-hosted server (the WSL2 recipe, whose linux package does carry the daemon
/// binary) opt back into the daemon-present arm.
/// </remarks>
public static class PersistentPtyDaemonGate
{
    /// <summary>
    /// Gets whether the persistentPty daemon is expected to be reachable, resolved from
    /// <c>OPENCODE_SDK_TESTS_PTY_DAEMON</c> and the current platform.
    /// </summary>
    public static bool DaemonExpected => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Test seam: resolves through an injected reader instead of the real process environment.
    /// </summary>
    internal static bool Resolve(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return read("OPENCODE_SDK_TESTS_PTY_DAEMON") switch
        {
            "1" => true,
            "0" => false,
            _ => !OperatingSystem.IsWindows(),
        };
    }
}
