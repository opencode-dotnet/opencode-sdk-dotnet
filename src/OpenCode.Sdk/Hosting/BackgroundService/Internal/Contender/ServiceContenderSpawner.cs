using static OpenCode.Sdk.Internal.WindowsPlatform;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="IServiceContenderSpawner"/> on the SDK's own spawn primitives, the ones
/// the standalone launcher uses: <see cref="WindowsSpawn"/> and <see cref="PosixSpawn"/>.
/// This file holds what both platforms share; the <c>.Windows</c> and <c>.Unix</c> files hold one
/// arm each.
/// </summary>
/// <param name="posixSpawn">The POSIX spawn the Unix arm starts contenders through.</param>
/// <param name="windowsSpawn">The Windows spawn the Windows arm starts contenders through.</param>
/// <param name="time">The clock the contenders' exit backoff runs on.</param>
internal sealed partial class ServiceContenderSpawner(IPosixSpawn posixSpawn, IWindowsSpawn windowsSpawn, TimeProvider time) : IServiceContenderSpawner
{
    /// <summary>The PTY-handoff variable whose value is secret-bearing: removed when empty, redacted from diagnostics when present.</summary>
    private const string HandoffVariable = "OPENCODE_PTY_HANDOFF";

    private readonly IPosixSpawn _posixSpawn = posixSpawn;
    private readonly IWindowsSpawn _windowsSpawn = windowsSpawn;
    private readonly TimeProvider _time = time;

    /// <inheritdoc />
    public IServiceContender Spawn(IServiceContenderSpawner.ContenderStartInfo startInfo) => Start(startInfo);

    /// <summary>
    /// Starts one detached contender. The seam's member forwards here, so the concrete contender —
    /// whose retained stderr text the spawner's own tests read — stays reachable without a cast.
    /// </summary>
    /// <param name="startInfo">What to spawn.</param>
    /// <returns>The contender.</returns>
    public ServiceContender Start(IServiceContenderSpawner.ContenderStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // The election hands over a resolved executable and an overlay the door already refused
        // blank keys in; NUL is the one thing the native string marshalling would silently cut.
        if (startInfo.Environment.Keys.Any(ContainsNul) ||
            startInfo.Environment.Values.Any(static value => value is not null && ContainsNul(value)))
        {
            throw new ArgumentException("ContenderStartInfo.Environment keys and values cannot contain NUL.", nameof(startInfo));
        }

        if (startInfo.Executable.IsBatchScript && !IsWindows)
        {
            throw new OpenCodeServerException(
                "The background service contender routes through cmd.exe, which exists only on Windows: no contender was started.");
        }

        var environment = MergeEnvironment(startInfo.Environment, out var redaction);
        return IsWindows
            ? SpawnWindows(startInfo, environment, redaction)
            : SpawnUnix(startInfo, environment, redaction);
    }

    /// <summary>
    /// The NUL scan the analyzer wall leaves: <c>Contains(char)</c> and <c>IndexOf(char)</c>
    /// trade CA1307/MA0001 against CA2249, and the <c>StringComparison</c> overload is absent
    /// downlevel, so the check is a plain loop.
    /// </summary>
    private static bool ContainsNul(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Layers exact overlay keys over the host's environment. On Windows the first spelling in
    /// ordinal order wins, as the pinned client's spawn does, before null values are removed.
    /// An empty handoff is removed rather than passed as an empty ticket.
    /// </summary>
    private static Dictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string?> overlay, out string? redaction)
    {
        var entries = ServiceContenderEnvironmentComposer.Overlay(Environment.GetEnvironmentVariables(), overlay);
        var merged = ServiceContenderEnvironmentComposer.Resolve(entries);

        redaction = null;
        if (merged.TryGetValue(HandoffVariable, out var ticket) && !string.IsNullOrEmpty(ticket))
        {
            redaction = ticket;
        }
        else
        {
            merged.Remove(HandoffVariable);
        }

        return merged;
    }

    /// <summary>Names the resolved target alongside the caller's spelling, when they differ — the launcher's failure posture, without any secret-bearing value.</summary>
    private static OpenCodeServerException SpawnFailure(
        IServiceContenderSpawner.ContenderStartInfo startInfo, Exception cause)
    {
        var command = startInfo.Executable.Command;
        var detail = string.Equals(command, startInfo.Executable.Path, StringComparison.Ordinal)
            ? command
            : command + " (resolved to '" + startInfo.Executable.Path + "')";
        return new OpenCodeServerException(
            $"Failed to start the background service contender '{detail}'.", cause);
    }

}
