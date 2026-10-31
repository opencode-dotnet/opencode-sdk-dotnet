namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Whether persistentPty tests should expect the <c>opencode-pty</c> daemon to be reachable.
/// </summary>
/// <remarks>
/// The daemon ships as the npm package <c>@opencode/pty</c>; at the pinned upstream
/// (<c>@opencode/pty</c> 0.3.3) it carries a win32 binary too, so the daemon is expected on every
/// platform. <c>OPENCODE_SDK_TESTS_PTY_DAEMON=0</c> still selects the daemon-absent arm
/// (<c>create</c> returns 503; the others take their existence-independent arms) for a host whose
/// server cannot start the daemon, and <c>1</c> forces the daemon-present arm.
/// </remarks>
public static class PersistentPtyDaemonGate
{
    /// <summary>
    /// Gets whether the persistentPty daemon is expected to be reachable, resolved from
    /// <c>OPENCODE_SDK_TESTS_PTY_DAEMON</c>; expected by default.
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
            _ => true,
        };
    }
}
