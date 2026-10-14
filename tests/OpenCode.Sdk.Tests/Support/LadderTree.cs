using System.Globalization;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The <c>ladder-tree.js</c> server stand-in: a root with the children a flag list asks for, each
/// reporting its pid on stderr before readiness, so the start's collector names every process the
/// test has to end, whatever the launcher did to the tree. Cleanup is by those pids, never by a
/// parent-pid walk: a child whose root exited has been reparented.
/// </summary>
internal static class LadderTree
{
    public const string Root = "root";
    public const string GroupChild = "group-child";
    public const string Detached = "detached";
    public const string Holder = "holder";
    public const string StderrHolder = "stderr-holder";

    private const string Prefix = "ladder-tree role=";

    /// <summary>How long cleanup waits for a process it signalled to be gone.</summary>
    private static readonly TimeSpan CleanupBound = TimeSpan.FromSeconds(10);

    /// <summary>The launch options for one tree.</summary>
    /// <param name="output">The collector the pids are read from.</param>
    /// <param name="grace">The shutdown grace.</param>
    /// <param name="flags">The fixture flags.</param>
    /// <returns>The options.</returns>
    public static OpenCodeServerOptions Options(OpenCodeServerOutput output, TimeSpan grace, params string[] flags) =>
        new()
        {
            Command = ["bun", "-e", new FixtureLoader().LoadText("Server.ladder-tree.js")],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENCODE_SDK_TEST_LADDER"] = string.Join(',', flags),
            },
            GracefulShutdownTimeout = grace,
            Output = output,
        };

    /// <summary>
    /// The launch options for one tree whose root exits, with the code an <c>exit-on-marker=N</c>
    /// flag names, once the marker file exists, so the test decides when the exit comes.
    /// </summary>
    /// <param name="output">The collector the pids are read from.</param>
    /// <param name="grace">The shutdown grace.</param>
    /// <param name="marker">The file whose existence ends the root.</param>
    /// <param name="flags">The fixture flags.</param>
    /// <returns>The options.</returns>
    public static OpenCodeServerOptions OptionsWithMarker(OpenCodeServerOutput output, TimeSpan grace, string marker, params string[] flags)
    {
        var options = Options(output, grace, flags);
        options.Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OPENCODE_SDK_TEST_LADDER"] = string.Join(',', flags),
            ["OPENCODE_SDK_TEST_LADDER_MARKER"] = marker,
        };
        return options;
    }

    /// <summary>Reads the one pid the tree reported for a role.</summary>
    /// <param name="output">The start's collector.</param>
    /// <param name="role">The role.</param>
    /// <returns>The pid.</returns>
    public static int Pid(OpenCodeServerOutput output, string role) =>
        Pids(output).TryGetValue(role, out var pid)
            ? pid
            : throw new InvalidOperationException(
                $"The ladder tree reported no '{role}' pid: " + string.Join(" | ", output.GetSnapshot().StandardError));

    /// <summary>
    /// Waits until the tree has reported the pid for a role. The collector raises no event when a
    /// line arrives, so the wait reads it again after a short pause; the arrival decides, and only a
    /// tree that never reports reaches the hang guard.
    /// </summary>
    /// <param name="output">The start's collector.</param>
    /// <param name="role">The role.</param>
    /// <param name="hangGuard">Bounds a tree that never reports; it decides nothing else.</param>
    /// <returns>The pid.</returns>
    [SlopwatchSuppress("SW004", "The collector raises no arrival event; the pause only paces a re-read, and the arrival decides")]
    public static async Task<int> WaitForPidAsync(OpenCodeServerOutput output, string role, TimeSpan hangGuard)
    {
        using var guard = new CancellationTokenSource(hangGuard);
        while (true)
        {
            if (Pids(output).TryGetValue(role, out var pid))
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), guard.Token);
        }
    }

    /// <summary>Reads every pid the tree reported, by role.</summary>
    /// <param name="output">The start's collector.</param>
    /// <returns>The pids.</returns>
    public static IReadOnlyDictionary<string, int> Pids(OpenCodeServerOutput output) =>
        Pids(output.GetSnapshot().StandardError);

    /// <summary>Reads every pid the tree reported, by role, from the stderr lines it wrote.</summary>
    /// <param name="standardError">The tree's stderr lines, among any others.</param>
    /// <returns>The pids.</returns>
    public static IReadOnlyDictionary<string, int> Pids(IEnumerable<string> standardError)
    {
        var pids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in standardError)
        {
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fields = line[Prefix.Length..].Split(' ');
            if (fields is [var role, var pidField] &&
                pidField.StartsWith("pid=", StringComparison.Ordinal) &&
                int.TryParse(pidField.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                pids[role] = pid;
            }
        }

        return pids;
    }

    /// <summary>Ends every process the tree reported that still runs, and waits for each to be gone.</summary>
    /// <param name="output">The start's collector.</param>
    /// <returns>The roles whose process did not end inside the cleanup bound; empty when all ended.</returns>
    public static Task<IReadOnlyList<string>> EndEveryReportedProcessAsync(OpenCodeServerOutput output) =>
        EndEveryReportedProcessAsync(output.GetSnapshot().StandardError);

    /// <summary>Ends every process the tree reported in its stderr lines that still runs, and waits for each to be gone.</summary>
    /// <param name="standardError">The tree's stderr lines, among any others.</param>
    /// <returns>The roles whose process did not end inside the cleanup bound; empty when all ended.</returns>
    public static async Task<IReadOnlyList<string>> EndEveryReportedProcessAsync(IEnumerable<string> standardError)
    {
        var survivors = new List<string>();
        foreach (var (role, pid) in Pids(standardError))
        {
            ProcessObservation.KillIfRunning(pid);
            if (!await ProcessObservation.ObserveExitWithinAsync(pid, CleanupBound, CancellationToken.None))
            {
                survivors.Add(role);
            }
        }

        return survivors;
    }
}
