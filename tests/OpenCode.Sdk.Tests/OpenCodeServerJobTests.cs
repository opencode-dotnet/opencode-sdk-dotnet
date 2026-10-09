using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The Windows placement against real processes: the server is created suspended, placed in the
/// process-wide kill-on-close job, and resumed. The job ends the server when its owner ends, however
/// the owner ends; the server's detached descendants stay outside it, while the children a libuv
/// server places in its own job end with it, because that job nests under ours; a job the host itself
/// runs in nests the other way. A placement that fails ends the suspended server and fails the
/// start. The owner is a separate host process (<see cref="LauncherHost"/>) wherever it must be
/// killed. Linux and macOS place the server in a session instead, which the process-group tests
/// prove; each test's POSIX arm says so. Every process a test starts is ended by its pid before the
/// test returns. Keyless <c>[NotInParallel]</c>: the proofs ride wall-clock bounds.
/// </summary>
[NotInParallel]
public sealed class OpenCodeServerJobTests
{
    private const string PrintLogsVariable = "OPENCODE_PRINT_LOGS";
    private const string LadderVariable = "OPENCODE_SDK_TEST_LADDER";

    /// <summary>The bound upstream's own standalone test gives a server to end with its killed owner.</summary>
    private static readonly TimeSpan OwnerDeathBound = TimeSpan.FromSeconds(5);

    /// <summary>How long a detached descendant has to keep running after its server ended.</summary>
    private static readonly TimeSpan SurvivalWindow = TimeSpan.FromSeconds(2);

    /// <summary>How long a test waits for a process it observes to end.</summary>
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static readonly RealFileSystem FileSystem = new();

    /// <summary>
    /// The owner is killed outright, so no disposal runs, and the stand-in never reads stdin, so the
    /// lease's end-of-stream cannot end it either: only the job can, when the kernel closes the
    /// killed owner's handle to it.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_End_A_Server_That_Ignores_Stdin_When_The_Owner_Is_Killed(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job; the owner tests prove the lease's end-of-stream ends the server there");
            return;
        }

        var tree = LadderTree.Options(new OpenCodeServerOutput(), Grace);
        var server = default(Process);
        try
        {
            await using var host = await LauncherHost.StartAsync([.. tree.Command], tree.Environment!, LauncherHostIgnores.Nothing, cancellationToken);
            server = Hold(host.ServerProcessId);

            var killed = host.Kill();
            var ownerEnded = await host.ExitedWithinAsync(ObservationBound);
            var serverEnded = await ProcessObservation.ObserveExitWithinAsync(server, OwnerDeathBound, cancellationToken);

            await Assert.That(killed).IsTrue();
            await Assert.That(ownerEnded).IsTrue();
            await Assert.That(serverEnded).IsTrue().Because(host.DescribeError());
            BranchReport.Print("Windows — owner " + host.ProcessId.ToString(CultureInfo.InvariantCulture) + " killed; the job ended server " + server.Id.ToString(CultureInfo.InvariantCulture) + " with code " + server.ExitCode.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            await EndAsync(server);
        }
    }

    /// <summary>
    /// What else the job takes with the owner: the server's plain child, which the server's own
    /// runtime placed in its job, nested under ours, ends with it; its detached child, which no job
    /// holds, keeps running, as upstream leaves it.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_End_Only_The_Servers_Joined_Children_When_The_Owner_Is_Killed(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job; the process-group tests prove the detached child survives there");
            return;
        }

        var tree = LadderTree.Options(new OpenCodeServerOutput(), Grace, LadderTree.GroupChild, LadderTree.Detached);
        var environment = WithPrintLogs(tree);
        IReadOnlyList<string> reported = [];
        try
        {
            await using var host = await LauncherHost.StartAsync([.. tree.Command], environment, LauncherHostIgnores.Nothing, cancellationToken);
            var pids = await ReportedPidsAsync(host, cancellationToken, LadderTree.Root, LadderTree.GroupChild, LadderTree.Detached);
            reported = host.ErrorLines;
            using var server = Hold(pids[LadderTree.Root]);
            using var groupChild = Hold(pids[LadderTree.GroupChild]);
            using var detached = Hold(pids[LadderTree.Detached]);

            _ = host.Kill();
            var serverEnded = await ProcessObservation.ObserveExitWithinAsync(server, OwnerDeathBound, cancellationToken);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, OwnerDeathBound, cancellationToken);
            var detachedSurvived = !await ProcessObservation.ObserveExitWithinAsync(detached, SurvivalWindow, cancellationToken);

            await Assert.That(serverEnded).IsTrue();
            await Assert.That(groupChildEnded).IsTrue();
            await Assert.That(detachedSurvived).IsTrue();
            BranchReport.Print("Windows — the owner's death ended the server and its joined child; the detached child " + detached.Id.ToString(CultureInfo.InvariantCulture) + " survived");
        }
        finally
        {
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(reported)).IsEmpty();
        }
    }

    /// <summary>
    /// The membership itself, asked of the kernel in the host that owns the job: the server is in
    /// it; its plain child is in it too, through the job the server's runtime nested under ours; its
    /// detached child is in no job of ours.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Place_The_Server_And_Its_Joined_Children_In_The_Job(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job; the server leads a session of its own there");
            return;
        }

        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartAsync(LadderTree.Options(output, Grace, LadderTree.GroupChild, LadderTree.Detached), cancellationToken);
        try
        {
            var job = KillOnCloseJob.ForCurrentProcess.Current!;
            using var root = Hold(server.ProcessId);
            using var groupChild = Hold(LadderTree.Pid(output, LadderTree.GroupChild));
            using var detached = Hold(LadderTree.Pid(output, LadderTree.Detached));

            await Assert.That(WindowsProcessProbe.IsInJob(root.SafeHandle, job)).IsTrue();
            await Assert.That(WindowsProcessProbe.IsInJob(groupChild.SafeHandle, job)).IsTrue();
            await Assert.That(WindowsProcessProbe.IsInJob(detached.SafeHandle, job)).IsFalse();
            BranchReport.Print("Windows — the server and its plain child are in the launcher's job; the detached child is not");
        }
        finally
        {
            await server.DisposeAsync();
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

    /// <summary>
    /// The server runs no instruction outside the job: with the assignment delayed, a stand-in that
    /// asks the kernel for its own job's limits as its very first act still sees exactly the
    /// launcher's four, because it was suspended until the assignment was done.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Place_The_Server_In_The_Job_Before_It_Runs(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job; the server is spawned straight into its session there");
            return;
        }

        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions { Command = [.. FixtureCommand("job-report")], Output = output },
            LauncherSeams.ForCurrentProcess() with { Job = new DelayedJob(KillOnCloseJob.ForCurrentProcess, TimeSpan.FromMilliseconds(500)) },
            cancellationToken);
        try
        {
            var report = (await LiveReadiness.WaitAsync(
                _ => Task.FromResult(output.GetSnapshot().StandardError),
                static lines => lines.Count > 0,
                "the stand-in's job report",
                cancellationToken))[0];

            await Assert.That(report).IsEqualTo("job limits=0x" + KillOnCloseJob.Limits.ToString("X", CultureInfo.InvariantCulture));
            BranchReport.Print("Windows — the stand-in's first act saw: " + report);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>
    /// A placement the kernel refuses for another reason than <c>ERROR_ACCESS_DENIED</c> fails the
    /// start, and the server, still suspended, is ended before it ran: nothing of it is left.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_End_The_Suspended_Server_When_The_Placement_Fails(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job to place the server in");
            return;
        }

        using var spawn = new HoldingSpawn();
        var failure = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions { Command = [.. LadderTree.Options(new OpenCodeServerOutput(), Grace).Command] },
            LauncherSeams.ForCurrentProcess() with { WindowsSpawn = spawn, Job = new RefusingJob(1816) },
            cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("could not be placed in the job");
        await Assert.That(failure.InnerException).IsTypeOf<Win32Exception>();
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(spawn.Held!, ObservationBound, cancellationToken)).IsTrue();
        await Assert.That(spawn.Held!.ExitCode).IsEqualTo(1);
    }

    /// <summary>A refusal with <c>ERROR_ACCESS_DENIED</c>, which libuv tolerates, still starts the server.</summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Start_The_Server_When_The_Placement_Is_Refused_With_Access_Denied(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no job to place the server in");
            return;
        }

        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            LadderTree.Options(output, Grace),
            LauncherSeams.ForCurrentProcess() with { Job = new RefusingJob(5) },
            cancellationToken);
        try
        {
            using var root = Hold(server.ProcessId);

            await Assert.That(root.HasExited).IsFalse();
            BranchReport.Print("Windows — the refused placement left server " + root.Id.ToString(CultureInfo.InvariantCulture) + " running, its lease the one tie to its owner");
        }
        finally
        {
            await server.DisposeAsync();
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

    /// <summary>A main thread that cannot be resumed fails the start, and the server is ended while still suspended.</summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_End_The_Server_When_It_Cannot_Be_Resumed(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the server is never suspended there");
            return;
        }

        using var spawn = new HoldingSpawn(breakMainThread: true);
        var failure = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions { Command = [.. LadderTree.Options(new OpenCodeServerOutput(), Grace).Command] },
            LauncherSeams.ForCurrentProcess() with { WindowsSpawn = spawn },
            cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("could not be resumed");
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(spawn.Held!, ObservationBound, cancellationToken)).IsTrue();
        await Assert.That(spawn.Held!.ExitCode).IsEqualTo(1);
    }

    /// <summary>
    /// The host itself runs in a job, as a host a CI runner, a service manager, or a Node client
    /// starts does. Without breakaway, the server starts in the host's job and the launcher's job
    /// nests under it; with silent breakaway, the server starts outside the host's job. Either way the
    /// launcher's job ends it when the owner is killed.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    [Arguments(0u, true)]
    [Arguments(0x800u, true)]
    [Arguments(0x1000u, false)]
    public async Task StartAsync_Should_End_The_Server_With_An_Owner_That_Runs_In_A_Job_Of_Its_Own(uint hostJobLimits, bool serverInHostJob, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no jobs");
            return;
        }

        using var hostJob = TestJob.Create(hostJobLimits);
        var tree = LadderTree.Options(new OpenCodeServerOutput(), Grace);
        var server = default(Process);
        try
        {
            await using var host = await LauncherHost.StartInJobAsync([.. tree.Command], tree.Environment!, hostJob.Handle, cancellationToken);
            server = Hold(host.ServerProcessId);
            var inHostJob = WindowsProcessProbe.IsInJob(server.SafeHandle, hostJob.Handle);

            _ = host.Kill();
            var serverEnded = await ProcessObservation.ObserveExitWithinAsync(server, OwnerDeathBound, cancellationToken);

            await Assert.That(inHostJob).IsEqualTo(serverInHostJob);
            await Assert.That(serverEnded).IsTrue().Because(host.DescribeError());
            BranchReport.Print("Windows — host job limits 0x" + hostJobLimits.ToString("X", CultureInfo.InvariantCulture) + ": the server " + (inHostJob ? "started in the host's job" : "broke away from it") + ", and the owner's death ended it");
        }
        finally
        {
            await EndAsync(server);
        }
    }

    /// <summary>
    /// The default npm install puts a batch shim first on PATH: the launcher's root is then cmd.exe,
    /// which the job holds, and the real server is cmd.exe's child, outside the job. When the owner is
    /// killed, the job ends cmd.exe, and the real server ends on its stdin lease's end-of-stream,
    /// the one channel left to it; a server that never reads stdin would outlive its owner, which
    /// is why the full guarantee needs <c>Command</c> to name the executable itself.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_End_A_Batch_Shims_Server_Through_The_Lease_When_The_Owner_Is_Killed(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — no batch shims");
            return;
        }

        using var runRoot = new TestRunRoot(FileSystem);
        var script = FileSystem.Path.Combine(runRoot.Path, "ladder-tree.js");
#if NET
        await FileSystem.File.WriteAllTextAsync(script, new FixtureLoader().LoadText("Server.ladder-tree.js"), new UTF8Encoding(false), cancellationToken);
#else
        FileSystem.File.WriteAllText(script, new FixtureLoader().LoadText("Server.ladder-tree.js"), new UTF8Encoding(false));
#endif
        using var shim = PathCommandShim.ForwardingTo(FileSystem, "opencode-sdk-test-shim.cmd", ["bun", script]);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PrintLogsVariable] = "1",
            [LadderVariable] = "watch-stdin",
        };
        IReadOnlyList<string> reported = [];
        try
        {
            await using var host = await LauncherHost.StartAsync(["opencode-sdk-test-shim"], environment, LauncherHostIgnores.Nothing, cancellationToken);
            var pids = await ReportedPidsAsync(host, cancellationToken, LadderTree.Root);
            reported = host.ErrorLines;
            using var interpreter = Hold(host.ServerProcessId);
            using var server = Hold(pids[LadderTree.Root]);

            _ = host.Kill();
            var interpreterEnded = await ProcessObservation.ObserveExitWithinAsync(interpreter, OwnerDeathBound, cancellationToken);
            var serverEnded = await ProcessObservation.ObserveExitWithinAsync(server, OwnerDeathBound, cancellationToken);

            await Assert.That(interpreter.Id).IsNotEqualTo(server.Id);
            await Assert.That(interpreterEnded).IsTrue();
            await Assert.That(serverEnded).IsTrue();
            await Assert.That(server.ExitCode).IsEqualTo(0);
            BranchReport.Print("Windows — the job ended cmd.exe " + interpreter.Id.ToString(CultureInfo.InvariantCulture) + "; its server " + server.Id.ToString(CultureInfo.InvariantCulture) + " left on the lease's end-of-stream with code 0");
        }
        finally
        {
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(reported)).IsEmpty();
        }
    }

    private static IReadOnlyList<string> FixtureCommand(params string[] mode)
    {
        var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
        return [fixture[0], fixture[1], .. mode];
    }

    private static Dictionary<string, string> WithPrintLogs(OpenCodeServerOptions tree)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [PrintLogsVariable] = "1" };
        foreach (var entry in tree.Environment!)
        {
            environment[entry.Key] = entry.Value;
        }

        return environment;
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReportedPidsAsync(LauncherHost host, CancellationToken cancellationToken, params string[] roles) =>
        await LiveReadiness.WaitAsync(
            _ => Task.FromResult(LadderTree.Pids(host.ErrorLines)),
            pids => roles.All(pids.ContainsKey),
            "the ladder tree's pids on the host's stderr",
            cancellationToken);

    /// <summary>Opens a process by pid and keeps a handle on it, so the pid cannot name another process while the test looks.</summary>
    private static Process Hold(int processId)
    {
        var process = Process.GetProcessById(processId);
        _ = process.Handle;
        return process;
    }

    private static async Task EndAsync(Process? server)
    {
        if (server is null)
        {
            return;
        }

        using (server)
        {
            if (!server.HasExited)
            {
                server.Kill();
            }

            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(server, ObservationBound, CancellationToken.None)).IsTrue();
        }
    }

    /// <summary>The shipped job, assigned only after a delay: a server that ran before its assignment would show it.</summary>
    private sealed class DelayedJob(IProcessJob job, TimeSpan delay) : IProcessJob
    {
        [SlopwatchSuppress(
            "SW004",
            "The delay is the subject under test: a server that ran before its assignment would show it in the window the delay opens.")]
        public JobAssignment Assign(SafeProcessHandle process)
        {
            Thread.Sleep(delay);
            return job.Assign(process);
        }
    }

    /// <summary>A job the kernel refuses with the given error: access denied is tolerated, anything else fails the start.</summary>
    private sealed class RefusingJob(int error) : IProcessJob
    {
        public JobAssignment Assign(SafeProcessHandle process) =>
            error == WindowsInterop.AccessDenied ? JobAssignment.Refused : throw new Win32Exception(error);
    }

    /// <summary>
    /// The shipped spawn, holding a handle of its own on each child so the test can read its end;
    /// optionally with the suspended child's main thread replaced by an empty handle, which no
    /// resume can reach.
    /// </summary>
    private sealed class HoldingSpawn(bool breakMainThread = false) : IWindowsSpawn, IDisposable
    {
        private readonly WindowsSpawn _spawn = new();

        public Process? Held { get; private set; }

        public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
        {
            var child = _spawn.Spawn(request);
            Held = Hold(child.ProcessId);
            if (!breakMainThread)
            {
                return child;
            }

            child.MainThread?.Dispose();
            return child with { MainThread = new SafeThreadHandle(IntPtr.Zero) };
        }

        public void Dispose()
        {
            if (Held is { HasExited: false })
            {
                Held.Kill();
            }

            Held?.Dispose();
        }
    }
}
