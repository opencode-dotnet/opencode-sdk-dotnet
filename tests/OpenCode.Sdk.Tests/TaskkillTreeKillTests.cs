#if NETFRAMEWORK
using System.Diagnostics;
using System.Globalization;
using System.Management;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The downlevel Windows tree kill against real process trees: taskkill's exit code is the result,
/// so a root that is already gone — whose descendants taskkill can no longer reach — reports the
/// kill incomplete, and a taskkill that outlasts its bound is ended before the call returns. The
/// tests find their taskkills through WMI, by parent and command line, so they only see the
/// processes they started.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class TaskkillTreeKillTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>
    /// How long an ended taskkill is given to leave the process table. The kill is asynchronous,
    /// and a loaded machine can take seconds to finish it.
    /// </summary>
    private static readonly TimeSpan EndedTaskkillExitBound = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The tree is the fixture's tree root and its idle child. taskkill exits zero only when it
    /// ended every member itself, so no member may end on its own when another member is ended: a
    /// root that exits once its child dies is gone before taskkill reaches it, and taskkill then
    /// reports that root as a failure although the whole tree ended. Neither fixture process waits
    /// on the other or touches its console, so each one runs until taskkill ends it.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Kill_Should_Report_Issued_When_Taskkill_Ends_The_Tree(CancellationToken cancellationToken)
    {
        await using var root = await ServiceFixtureProcess.StartTreeRootAsync(FileSystem, cancellationToken);
        Process? child = null;
        try
        {
            // The root holds a handle on its child while it runs, so the pid it named is still the
            // child when this opens it; the handle taken here then keeps that pid from being
            // reused until the test is done with it.
            child = TryOpen(root.ChildProcessId);
            await Assert.That(child).IsNotNull();

            var result = new TaskkillTreeKill(Bound).Kill(root.ProcessId);

            await Assert.That(result).IsEqualTo(ProcessTreeKillResult.Issued);
            await Assert.That(await root.ObserveExitWithinAsync(Bound, cancellationToken)).IsTrue();
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(child!, Bound, cancellationToken)).IsTrue();
        }
        finally
        {
            // The root's disposal ends the root; the child outlives a root that is ended alone, so
            // it is ended here, through the handle that names exactly the recorded process.
            if (child is not null)
            {
                using (child)
                {
                    await EndIfRunningAsync(child);
                }
            }
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Kill_Should_Report_Incomplete_When_The_Root_Already_Exited(CancellationToken cancellationToken)
    {
        // The held process object keeps the exited root's pid from being reused meanwhile.
        using var root = StartTree("exit 0");
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(root, Bound, cancellationToken)).IsTrue();

        var result = new TaskkillTreeKill(Bound).Kill(root.Id);

        await Assert.That(result).IsEqualTo(ProcessTreeKillResult.Incomplete);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Kill_Should_End_A_Taskkill_That_Outlasts_Its_Bound(CancellationToken cancellationToken)
    {
        using var root = StartTree("exit 0");
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(root, Bound, cancellationToken)).IsTrue();

        var result = new TaskkillTreeKill(TimeSpan.Zero).Kill(root.Id);

        await Assert.That(result).IsEqualTo(ProcessTreeKillResult.Incomplete);
        await Assert.That(await TaskkillsLeftAgainstAsync(root.Id, EndedTaskkillExitBound, cancellationToken)).IsEmpty();
    }

    private static Process StartTree(string command) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = BatchCommandLine.InterpreterPath,
            Arguments = "/d /c " + command,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    private static async Task EndIfRunningAsync(Process process)
    {
        if (!process.HasExited)
        {
            _ = TestProcessTreeKill.TryKill(process);
            _ = await ProcessObservation.ObserveExitWithinAsync(process, Bound, CancellationToken.None);
        }
    }

    /// <summary>
    /// Waits at most <paramref name="bound"/> for every taskkill this test process started against
    /// <paramref name="targetId"/> to leave the process table.
    /// </summary>
    /// <returns>The pids of the matching taskkills still present when the bound expired.</returns>
    private static async Task<List<int>> TaskkillsLeftAgainstAsync(int targetId, TimeSpan bound, CancellationToken cancellationToken)
    {
        var target = " /pid " + targetId.ToString(CultureInfo.InvariantCulture) + " ";
        string query;
        using (var self = Process.GetCurrentProcess())
        {
            query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'taskkill.exe' AND ParentProcessId = "
                + self.Id.ToString(CultureInfo.InvariantCulture);
        }

        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var left = Query(query)
                .Where(candidate => candidate.CommandLine?.IndexOf(target, StringComparison.Ordinal) >= 0)
                .Select(candidate => candidate.ProcessId)
                .ToList();
            if (left.Count == 0 || elapsed.Elapsed >= bound)
            {
                return left;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static List<(int ProcessId, string? CommandLine)> Query(string query)
    {
        var found = new List<(int ProcessId, string? CommandLine)>();
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();
        foreach (var result in results)
        {
            using (result)
            {
                found.Add(((int)(uint)result["ProcessId"], result["CommandLine"] as string));
            }
        }

        return found;
    }

    [SlopwatchSuppress(
        "SW003",
        "A child that exits between the query and the open is not the live child the search waits for; the search polls again.")]
    private static Process? TryOpen(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);

            // GetProcessById opens and closes a handle; reading Handle keeps one open, so the pid
            // cannot be reused by an unrelated process while the test still acts on it.
            _ = process.Handle;
            return process;
        }
        catch (ArgumentException)
        {
            // It exited between the query and the open.
            return null;
        }
    }
}
#endif
