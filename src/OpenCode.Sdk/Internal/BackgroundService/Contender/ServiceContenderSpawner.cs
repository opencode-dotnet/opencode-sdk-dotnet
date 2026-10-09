using System.Collections;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Posix.Abstractions;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using static OpenCode.Sdk.Internal.Windows.WindowsPlatform;

namespace OpenCode.Sdk.Internal.BackgroundService.Contender;

/// <summary>
/// The shipped <see cref="IServiceContenderSpawner"/> on the SDK's own spawn primitives, the ones
/// the standalone launcher uses: <see cref="Windows.WindowsSpawn"/> and <see cref="Posix.PosixSpawn"/>.
/// This file holds what both platforms share; the <c>.Windows</c> and <c>.Unix</c> files hold one
/// arm each.
/// </summary>
/// <param name="posixSpawn">The POSIX spawn the Unix arm starts contenders through.</param>
/// <param name="windowsSpawn">The Windows spawn the Windows arm starts contenders through.</param>
internal sealed partial class ServiceContenderSpawner(IPosixSpawn posixSpawn, IWindowsSpawn windowsSpawn) : IServiceContenderSpawner
{
    /// <summary>The PTY-handoff variable whose value is secret-bearing: removed when empty, redacted from diagnostics when present.</summary>
    private const string HandoffVariable = "OPENCODE_PTY_HANDOFF";

    private readonly IPosixSpawn _posixSpawn = posixSpawn;
    private readonly IWindowsSpawn _windowsSpawn = windowsSpawn;

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
    /// Layers the overlay over the launching process's own environment, the way the pinned client
    /// spreads <c>process.env</c> under its service env. A null overlay value removes the variable;
    /// an empty handoff is removed too rather than passed as an empty ticket.
    /// </summary>
    private static Dictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string?> overlay, out string? redaction)
    {
        var merged = new Dictionary<string, string>(
            IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                merged[key] = value;
            }
        }

        foreach (var entry in overlay)
        {
            if (entry.Value is null)
            {
                merged.Remove(entry.Key);
            }
            else
            {
                merged[entry.Key] = entry.Value;
            }
        }

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
