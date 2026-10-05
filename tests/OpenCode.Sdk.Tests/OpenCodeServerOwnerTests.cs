using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The standalone server against what happens to its owner on Linux and macOS: a Ctrl+C sent to
/// the owner's process group, the owner killed outright, and an owner that runs with signals
/// ignored or blocked. The owner is a separate host process (<see cref="LauncherHost"/>) wherever
/// the owner itself must be signalled. Windows has no process groups or signal dispositions; each
/// Windows arm asserts the unchanged start and disposal instead. Every process a test starts is
/// ended by its pid before the test returns. Keyless <c>[NotInParallel]</c>: the proofs ride
/// wall-clock bounds.
/// </summary>
[NotInParallel]
public sealed class OpenCodeServerOwnerTests
{
    private const string MarkerVariable = "OPENCODE_SDK_TEST_MARKER";
    private const string StateVariable = "OPENCODE_SDK_TEST_STATE";
    private const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";

    private static readonly RealFileSystem FileSystem = new();

    /// <summary>How long a test waits for a process it observes to end.</summary>
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a server that would have received the owner's interrupt has to show it by exiting:
    /// the handler runs as the signal arrives, far inside this.
    /// </summary>
    private static readonly TimeSpan InterruptWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A Ctrl+C on the owner's terminal is <c>SIGINT</c> to the owner's foreground group. The server
    /// leads its own session, so it is in no group of the owner's: the interrupt ends the owner and
    /// never reaches the server. The positive control sends the same signal to the server's own
    /// group and sees the stand-in's handler run, so the absent marker is not a handler that was
    /// never installed. The stand-in ignores stdin, so the end of its lease does not end it first.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Keep_The_Server_Out_Of_The_Owners_Process_Group(CancellationToken cancellationToken)
    {
        const string standIn =
            "process.on('SIGINT', () => { require('node:fs').writeFileSync(process.env." + MarkerVariable + ", 'SIGINT'); process.exit(0); }); "
            + "console.log('" + ReadyLine + "'); setInterval(() => {}, 1000);";
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartAndDisposalAsync(["bun", "-e", standIn], cancellationToken);
            return;
        }

        using var runRoot = new TestRunRoot(FileSystem);
        var marker = FileSystem.Path.Combine(runRoot.Path, "interrupted");
        var serverPid = 0;
        try
        {
            await using var host = await LauncherHost.StartAsync(
                ["bun", "-e", standIn],
                new Dictionary<string, string>(StringComparer.Ordinal) { [MarkerVariable] = marker },
                LauncherHostIgnores.Nothing,
                cancellationToken);
            serverPid = host.ServerProcessId;

            var interruptedOwner = PosixProcessProbe.SignalGroup(host.ProcessId, PosixProcessProbe.Interrupt);
            var ownerEnded = await host.ExitedWithinAsync(ObservationBound);
            var serverEndedByInterrupt = await ProcessObservation.ObserveExitWithinAsync(serverPid, InterruptWindow, cancellationToken);
            var markerAfterOwnerInterrupt = FileSystem.File.Exists(marker);

            var interruptedServer = PosixProcessProbe.SignalGroup(serverPid, PosixProcessProbe.Interrupt);
            var markerAfterServerInterrupt = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(FileSystem.File.Exists(marker)), static exists => exists, "the stand-in's SIGINT marker", cancellationToken);

            await Assert.That(interruptedOwner).IsTrue();
            await Assert.That(ownerEnded).IsTrue().Because(host.DescribeError());
            await Assert.That(serverEndedByInterrupt).IsFalse();
            await Assert.That(markerAfterOwnerInterrupt).IsFalse();
            await Assert.That(interruptedServer).IsTrue();
            await Assert.That(markerAfterServerInterrupt).IsTrue();
            Console.WriteLine("branch: POSIX — SIGINT to owner group " + host.ProcessId.ToString(CultureInfo.InvariantCulture) + " ended the owner and missed server " + serverPid.ToString(CultureInfo.InvariantCulture) + "; SIGINT to the server's own group reached it");
        }
        finally
        {
            await EndAsync(serverPid);
        }
    }

    /// <summary>
    /// The owner is killed outright, so no disposal runs: the stdin lease is the only channel left,
    /// and the operating system closes it with the owner. The stand-in exits on its end-of-stream,
    /// as the real server does; a lease end held open anywhere else would keep it running.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Let_The_Server_See_End_Of_Stream_When_The_Owner_Is_Killed(CancellationToken cancellationToken)
    {
        const string standIn =
            "console.log('" + ReadyLine + "'); process.stdin.resume(); process.stdin.on('end', () => process.exit(0)); setInterval(() => {}, 1000);";
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartAndDisposalAsync(["bun", "-e", standIn], cancellationToken);
            return;
        }

        var serverPid = 0;
        try
        {
            await using var host = await LauncherHost.StartAsync(
                ["bun", "-e", standIn], new Dictionary<string, string>(StringComparer.Ordinal), LauncherHostIgnores.Nothing, cancellationToken);
            serverPid = host.ServerProcessId;
            var runningBefore = ProcessObservation.IsRunning(serverPid);

            var killed = PosixProcessProbe.SignalGroup(host.ProcessId, PosixProcessProbe.Kill);
            var ownerEnded = await host.ExitedWithinAsync(ObservationBound);
            var serverEnded = await ProcessObservation.ObserveExitWithinAsync(serverPid, ObservationBound, cancellationToken);

            await Assert.That(runningBefore).IsTrue();
            await Assert.That(killed).IsTrue();
            await Assert.That(ownerEnded).IsTrue();
            await Assert.That(serverEnded).IsTrue();
            Console.WriteLine("branch: POSIX — owner " + host.ProcessId.ToString(CultureInfo.InvariantCulture) + " killed; server " + serverPid.ToString(CultureInfo.InvariantCulture) + " left on its lease's end-of-stream");
        }
        finally
        {
            await EndAsync(serverPid);
        }
    }

    /// <summary>
    /// The .NET host ignores <c>SIGPIPE</c>, and a child <c>Process</c> starts keeps that ignore,
    /// which the positive control shows with the same report. The server starts with every signal
    /// at its default disposition and an empty mask, as libuv starts a Node child.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Start_The_Server_With_Default_Signal_State(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartAndDisposalAsync(["bun", "-e", "console.log('" + ReadyLine + "'); setInterval(() => {}, 1000);"], cancellationToken);
            return;
        }

        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = [.. SignalStateReport.ToStandardOutput()], Output = output }, cancellationToken);
        string state;
        try
        {
            // The report process exits once it wrote; its output ends with it.
            await server.OutputReadersEnded.WaitAsync(ObservationBound, cancellationToken);
            state = string.Join('\n', output.GetSnapshot().StandardOutput.Skip(1));
        }
        finally
        {
            await server.DisposeAsync();
        }

        var control = await ProcessReportAsync(SignalStateReport.Report, cancellationToken);
        if (OperatingSystem.IsLinux())
        {
            await Assert.That(PosixProcessProbe.StatusMask(state, "SigIgn:") & PosixProcessProbe.StandardSignals).IsEqualTo(0UL).Because(state);
            await Assert.That(PosixProcessProbe.StatusMask(state, "SigBlk:")).IsEqualTo(0UL).Because(state);
            await Assert.That(PosixProcessProbe.StatusMask(control, "SigIgn:") & SignalBit(Pipe)).IsNotEqualTo(0UL).Because(control);
        }
        else
        {
            await Assert.That(state.Trim()).IsEqualTo(SignalStateReport.MacDefaultState);
            await Assert.That(control).Contains("PIPE=IGNORE");
        }

        Console.WriteLine("branch: POSIX — server: " + state.Replace('\n', ' ') + "; Process child: " + control.Replace('\n', ' '));
    }

    /// <summary>
    /// An owner started with <c>SIGINT</c> ignored (under <c>nohup</c>, or a shell's <c>trap '' INT</c>)
    /// keeps the ignore, and so would a child that only inherits it. The server is reset to the
    /// default regardless, so a Ctrl+C aimed at it still ends it.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Reset_A_Signal_The_Owner_Ignores(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartAndDisposalAsync(["bun", "-e", "console.log('" + ReadyLine + "'); setInterval(() => {}, 1000);"], cancellationToken);
            return;
        }

        using var runRoot = new TestRunRoot(FileSystem);
        var stateFile = FileSystem.Path.Combine(runRoot.Path, "server-signal-state");
        var serverPid = 0;
        try
        {
            await using var host = await LauncherHost.StartAsync(
                SignalStateReport.ToFileIn(StateVariable),
                new Dictionary<string, string>(StringComparer.Ordinal) { [StateVariable] = stateFile },
                LauncherHostIgnores.Interrupt,
                cancellationToken);
            serverPid = host.ServerProcessId;
            var ownerState = OperatingSystem.IsLinux()
                ? await ReadTextAsync("/proc/" + host.ProcessId.ToString(CultureInfo.InvariantCulture) + "/status", cancellationToken)
                : await ProcessReportAsync("trap '' INT; exec " + SignalStateReport.Report, cancellationToken);
            var state = await LiveReadiness.WaitAsync(
                async token => FileSystem.File.Exists(stateFile) ? await ReadTextAsync(stateFile, token) : string.Empty,
                static text => text.Length > 0 && text[^1] == '\n',
                "the server's signal report",
                cancellationToken);

            if (OperatingSystem.IsLinux())
            {
                await Assert.That(PosixProcessProbe.StatusMask(ownerState, "SigIgn:") & SignalBit(Interrupt)).IsNotEqualTo(0UL);
                await Assert.That(PosixProcessProbe.StatusMask(state, "SigIgn:") & PosixProcessProbe.StandardSignals).IsEqualTo(0UL).Because(state);
                await Assert.That(PosixProcessProbe.StatusMask(state, "SigBlk:")).IsEqualTo(0UL).Because(state);
            }
            else
            {
                await Assert.That(ownerState).Contains("INT=IGNORE");
                await Assert.That(state.Trim()).IsEqualTo(SignalStateReport.MacDefaultState);
            }

            Console.WriteLine("branch: POSIX — the owner ignores SIGINT; server " + serverPid.ToString(CultureInfo.InvariantCulture) + ": " + state.Trim().Replace('\n', ' '));
        }
        finally
        {
            await EndAsync(serverPid);
        }
    }

    /// <summary>
    /// An owner that ignores <c>SIGCHLD</c>, as a host started with it ignored does. The launcher
    /// reads the ignore from the host's own <c>struct sigaction</c>, whose layout differs between the
    /// two kernels, and probes for the server's exit instead of blocking in a wait. On Linux the
    /// kernel reaps the exited server itself, so no status is left to read: the exit is unknown, and
    /// disposal probes the group and ends the member the server left behind. On macOS an ignore
    /// inherited across <c>exec</c> still leaves the exited server a zombie, and the probe reaps it
    /// with its real status: code 0, after which disposal leaves the group alone, as upstream does.
    /// The server writes to the host's stderr, where the test reads the pids it reports.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Decide_By_The_Exit_It_Observed_When_The_Owner_Ignores_Sigchld(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartAndDisposalAsync(["bun", "-e", "console.log('" + ReadyLine + "'); setInterval(() => {}, 1000);"], cancellationToken);
            return;
        }

        var tree = LadderTree.Options(new OpenCodeServerOutput(), ObservationBound, LadderTree.GroupChild, "exit-after-ready=0");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_PRINT_LOGS"] = "1" };
        foreach (var entry in tree.Environment!)
        {
            environment[entry.Key] = entry.Value;
        }

        var running = await LauncherHost.StartAsync([.. tree.Command], environment, LauncherHostIgnores.ChildExit, cancellationToken);
        try
        {
            var reaping = await running.ReadReportAsync(cancellationToken);
            var groupChild = (await LiveReadiness.WaitAsync(
                _ => Task.FromResult(LadderTree.Pids(running.ErrorLines)),
                static pids => pids.ContainsKey(LadderTree.GroupChild),
                "the ladder tree's group child pid",
                cancellationToken))[LadderTree.GroupChild];

            // The server exits on its own shortly after readiness; the host reports the exit it observed.
            var exit = await running.ReadReportAsync(cancellationToken);
            await running.ReleaseLeaseAsync();
            var ownerExited = await running.ExitedWithinAsync(ObservationBound);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(
                groupChild, OperatingSystem.IsLinux() ? ObservationBound : InterruptWindow, cancellationToken);

            await Assert.That(reaping).IsEqualTo("children reaped automatically=True").Because(running.DescribeError());
            await Assert.That(ownerExited).IsTrue().Because(running.DescribeError());
            if (OperatingSystem.IsLinux())
            {
                await Assert.That(exit).IsEqualTo("server exit=exited with an unknown status");
                await Assert.That(groupChildEnded).IsTrue();
            }
            else
            {
                await Assert.That(exit).IsEqualTo("server exit=exited with code 0");
                await Assert.That(groupChildEnded).IsFalse();
            }

            Console.WriteLine("branch: POSIX — the owner ignores SIGCHLD; " + exit + "; the group child " + (groupChildEnded ? "was ended" : "was left running"));
        }
        finally
        {
            await running.DisposeAsync();
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(running.ErrorLines)).IsEmpty();
        }
    }

    /// <summary><c>SIGINT</c>, the same on Linux and macOS.</summary>
    private const int Interrupt = 2;

    /// <summary><c>SIGPIPE</c>, the same on Linux and macOS.</summary>
    private const int Pipe = 13;

    private static ulong SignalBit(int signal) => 1UL << (signal - 1);

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
#if NET
        return await FileSystem.File.ReadAllTextAsync(path, cancellationToken);
#else
        return await Task.FromResult(FileSystem.File.ReadAllText(path)).WaitAsync(cancellationToken);
#endif
    }

    /// <summary>Runs a report the way <c>Process</c> starts a child, which keeps the host's ignored signals.</summary>
    private static async Task<string> ProcessReportAsync(string script, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
#if NET
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
#else
        startInfo.Arguments = Internal.ProcessArgumentComposer.Compose(["-c", script]);
#endif
        using var report = Process.Start(startInfo) ?? throw new InvalidOperationException("The report did not start.");
#if NET
        var text = await report.StandardOutput.ReadToEndAsync(cancellationToken);
#else
        var text = await report.StandardOutput.ReadToEndAsync().WaitAsync(cancellationToken);
#endif
        await report.WaitForExitAsync(cancellationToken);
        return text;
    }

    private static async Task AssertWindowsStartAndDisposalAsync(IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = [.. command], GracefulShutdownTimeout = TimeSpan.Zero }, cancellationToken);
        await server.DisposeAsync();
        await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsFalse();
        Console.WriteLine("branch: Windows — no process groups or signal dispositions; pid " + server.ProcessId.ToString(CultureInfo.InvariantCulture) + " started and was ended");
    }

    /// <summary>Ends a server the host started, by its pid, when it still runs.</summary>
    private static async Task EndAsync(int serverPid)
    {
        if (serverPid > 1)
        {
            ProcessObservation.KillIfRunning(serverPid);
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(serverPid, ObservationBound, CancellationToken.None)).IsTrue();
        }
    }
}
