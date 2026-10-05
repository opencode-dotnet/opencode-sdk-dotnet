using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The pipes a standalone start creates, on Linux and macOS. Each server receives only its own
/// three pipes, even when several starts and <c>Process</c> children are created at once; and every
/// path a start can take, successful or failed, leaves none of the pipes it created open in the
/// host. Windows keeps today's <c>Process</c> pipes, and each Windows arm asserts that the same
/// starts still succeed and fail as they did. Keyless <c>[NotInParallel]</c>: the host's descriptor
/// table is the subject, so no other test may open pipes while one runs.
/// </summary>
[NotInParallel]
public sealed class OpenCodeServerDescriptorTests
{
    private const int NoHang = 1;

    private const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";
    private const string PidFileVariable = "OPENCODE_SDK_TEST_PID_FILE";
    private const int Servers = 4;
    private const int ProcessChildren = 16;

    private static readonly RealFileSystem FileSystem = new();
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Several starts at once, while <c>Process</c> starts children of its own. A pipe end that
    /// crossed into a child other than its own server would hold that server's lease or output
    /// open after the host let go of it. On Linux every pipe is close-on-exec from its creation,
    /// so no child, server or not, holds another server's pipe. On macOS a pipe is marked
    /// close-on-exec only after it is created, so the launcher closes every descriptor its file
    /// actions do not name in the child: each server holds its three pipes and nothing else. A
    /// <c>Process</c> child there can still inherit a pipe created at the instant it forks, which
    /// nothing in managed code can prevent, so only the servers are checked on macOS.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task StartAsync_Should_Give_Each_Concurrent_Server_Only_Its_Own_Pipes(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsConcurrentStartsAsync(cancellationToken);
            return;
        }

        var starts = Enumerable.Range(0, Servers).Select(_ => OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = ["/bin/sh", "-c", "printf '%s\\n' '" + ReadyLine + "'; exec sleep 60"] },
            cancellationToken)).ToArray();
        var children = Enumerable.Range(0, ProcessChildren).Select(_ => Task.Run(StartSleepingChild, cancellationToken)).ToArray();
        var servers = new List<OpenCodeServer>();
        var childProcesses = new List<Process>();
        try
        {
            await SettleAsync(starts, children, servers, childProcesses);
            var serverDescriptors = new List<Dictionary<int, OpenDescriptor>>();
            foreach (var server in servers)
            {
                serverDescriptors.Add(await HostDescriptors.DescriptorsOfAsync(server.ProcessId));
            }

            var standard = serverDescriptors
                .SelectMany(static descriptors => descriptors.Where(static entry => entry.Key <= 2).Select(static entry => entry.Value))
                .ToList();
            await Assert.That(standard).Count().IsEqualTo(Servers * 3);
            await Assert.That(standard.TrueForAll(static pipe => pipe.IsPipe)).IsTrue();
            await Assert.That(standard.Select(static pipe => pipe.Target).Distinct(StringComparer.Ordinal).Count()).IsEqualTo(Servers * 3);
            if (OperatingSystem.IsMacOS())
            {
                foreach (var descriptors in serverDescriptors)
                {
                    await Assert.That(descriptors.Keys.Order().ToArray()).IsEquivalentTo([0, 1, 2], CollectionOrdering.Matching);
                }
            }
            else
            {
                var everyChild = serverDescriptors.ToList();
                foreach (var child in childProcesses)
                {
                    everyChild.Add(await HostDescriptors.DescriptorsOfAsync(child.Id));
                }

                foreach (var descriptors in everyChild)
                {
                    var foreign = descriptors
                        .Where(entry => entry.Key > 2 && standard.Exists(pipe => pipe.SharesPipeWith(entry.Value)))
                        .Select(static entry => entry.Key.ToString(CultureInfo.InvariantCulture) + " -> " + entry.Value)
                        .ToList();
                    await Assert.That(foreign).IsEmpty();
                }
            }

            Console.WriteLine("branch: POSIX — " + string.Join(" | ", serverDescriptors.Select(static descriptors => string.Join(',', descriptors.Keys.Order()))));
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }

            foreach (var child in childProcesses)
            {
                using (child)
                {
                    child.Kill();
                    _ = await ProcessObservation.ObserveExitWithinAsync(child, ObservationBound, CancellationToken.None);
                }
            }
        }
    }

    /// <summary>
    /// Waits for every start and child, and keeps each one that succeeded for the caller's cleanup
    /// even when another failed; the first failure then propagates.
    /// </summary>
    private static async Task SettleAsync(
        Task<OpenCodeServer>[] starts, Task<Process>[] children, List<OpenCodeServer> servers, List<Process> childProcesses)
    {
        try
        {
            await Task.WhenAll(Task.WhenAll(starts), Task.WhenAll(children));
        }
        finally
        {
            servers.AddRange(await SucceededAsync(starts));
            childProcesses.AddRange(await SucceededAsync(children));
        }
    }

    /// <summary>The results of the tasks that ran to completion; every task has completed.</summary>
    private static async Task<List<T>> SucceededAsync<T>(IEnumerable<Task<T>> tasks)
    {
        var results = new List<T>();
        foreach (var task in tasks.Where(static task => task.Status is TaskStatus.RanToCompletion))
        {
            results.Add(await task);
        }

        return results;
    }

    /// <summary>A child the way <c>Process</c> starts one, running until the test ends it.</summary>
    private static Process StartSleepingChild() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"exec sleep 60\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("The child did not start.");

    /// <summary>
    /// Upstream's <c>OPENCODE_PRINT_LOGS=1</c>, read from the host's environment as upstream reads
    /// it: the server's stderr is then the host's own stderr, the same open file, instead of a pipe
    /// the launcher reads, so nothing of it is collected. Without the variable the same stand-in's
    /// stderr is a launcher pipe and its line is collected, which shows the two apart. Windows keeps
    /// today's piped stderr until its own strategy reads the variable, and its arm asserts that.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Hand_The_Server_The_Hosts_Stderr_When_The_Host_Asks_For_Logs(CancellationToken cancellationToken)
    {
        const string standIn = "console.log('" + ReadyLine + "'); console.error('server stderr line'); setInterval(() => {}, 1000);";
        var printing = LauncherSeams.ForCurrentProcess();
        var hostEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in printing.HostEnvironment)
        {
            hostEnvironment[entry.Key] = entry.Value;
        }

        hostEnvironment["OPENCODE_PRINT_LOGS"] = "1";
        printing = printing with { HostEnvironment = hostEnvironment };

        var printed = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions { Command = ["bun", "-e", standIn], Output = printed }, printing, cancellationToken);
        OpenDescriptor? printedStderr = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var lines = await LiveReadiness.WaitAsync(
                    _ => Task.FromResult(printed.GetSnapshot().StandardError), static lines => lines.Count > 0, "the collected stderr line", cancellationToken);
                Console.WriteLine("branch: Windows — stderr stays a launcher pipe; collected: " + string.Join(" | ", lines));
                return;
            }

            printedStderr = (await HostDescriptors.DescriptorsOfAsync(server.ProcessId))[2];
        }
        finally
        {
            await server.DisposeAsync();
        }

        var collected = new OpenCodeServerOutput();
        var control = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = ["bun", "-e", standIn], Output = collected }, cancellationToken);
        OpenDescriptor controlStderr;
        try
        {
            _ = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(collected.GetSnapshot().StandardError), static lines => lines.Count > 0, "the collected stderr line", cancellationToken);
            controlStderr = (await HostDescriptors.DescriptorsOfAsync(control.ProcessId))[2];
        }
        finally
        {
            await control.DisposeAsync();
        }

        var hostStderr = (await HostDescriptors.DescriptorsOfAsync(HostDescriptors.CurrentProcessId()))[2];
        await Assert.That(printedStderr).IsEqualTo(hostStderr);
        await Assert.That(printed.GetSnapshot().StandardError).IsEmpty();
        await Assert.That(controlStderr).IsNotEqualTo(hostStderr);
        await Assert.That(controlStderr.IsPipe).IsTrue();
        Console.WriteLine("branch: POSIX — with OPENCODE_PRINT_LOGS=1 the server's stderr is the host's (" + hostStderr + "); without it, a launcher pipe (" + controlStderr + ")");
    }

    /// <summary>
    /// Every path a start can take leaves none of the pipes it created open in the host: the pipes
    /// new since a snapshot taken before the start are counted after the path ends. The normal close
    /// also counts them while the server runs, the positive control that shows the snapshot sees a
    /// start's pipes at all.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    [Arguments("normal close")]
    [Arguments("readiness timeout")]
    [Arguments("cancellation")]
    [Arguments("exit before readiness")]
    [Arguments("stdout closed before readiness")]
    [Arguments("non-contract first line")]
    [Arguments("spawn failure")]
    [Arguments("missing working directory")]
    public async Task StartAsync_Should_Close_Every_Pipe_It_Created_On_Every_Path(string path, CancellationToken cancellationToken)
    {
        using var runRoot = new TestRunRoot(FileSystem);
        var output = new OpenCodeServerOutput();
        var pidFile = FileSystem.Path.Combine(runRoot.Path, "stand-in.pid");
        var before = OperatingSystem.IsWindows() ? [] : await HostDescriptors.PipesAsync();
        var whileRunning = 0;
        try
        {
            whileRunning = await RunPathAsync(path, runRoot, pidFile, output, before, cancellationToken);
        }
        finally
        {
            _ = await LadderTree.EndEveryReportedProcessAsync(output);
            await EndReportedStandInAsync(pidFile);
        }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("branch: Windows — the " + path + " path ran on Process");
            return;
        }

        var after = await HostDescriptors.PipesAsync();
        var leaked = NewPipes(before, after);
        await Assert.That(leaked).IsEmpty();
        if (string.Equals(path, "normal close", StringComparison.Ordinal))
        {
            await Assert.That(whileRunning).IsGreaterThanOrEqualTo(3);
        }

        Console.WriteLine("branch: POSIX — the " + path + " path left no pipe open (" + whileRunning.ToString(CultureInfo.InvariantCulture) + " new while running)");
    }

    private static async Task<int> RunPathAsync(
        string path,
        TestRunRoot runRoot,
        string pidFile,
        OpenCodeServerOutput output,
        IReadOnlyDictionary<int, OpenDescriptor> before,
        CancellationToken cancellationToken)
    {
        // The stand-in writes its pid before anything else, so the cleanup can end it by its pid
        // on every path, whatever the launcher did to it.
        const string reportPid = "require('node:fs').writeFileSync(process.env." + PidFileVariable + ", String(process.pid)); ";
        var silent = new OpenCodeServerOptions
        {
            Command = ["bun", "-e", reportPid + "setInterval(() => {}, 1000);"],
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { [PidFileVariable] = pidFile },
            ReadinessTimeout = TimeSpan.FromSeconds(2),
            Output = output,
        };
        switch (path)
        {
            case "normal close":
                var server = await OpenCodeServer.StartAsync(LadderTree.Options(output, TimeSpan.FromSeconds(5)), cancellationToken);
                var running = OperatingSystem.IsWindows() ? 0 : NewPipes(before, await HostDescriptors.PipesAsync()).Count;
                await server.DisposeAsync();
                return running;
            case "readiness timeout":
                _ = await Assert.That(async () => await OpenCodeServer.StartAsync(silent, cancellationToken)).Throws<OpenCodeServerException>();
                return 0;
            case "cancellation":
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    silent.ReadinessTimeout = TimeSpan.FromSeconds(60);
                    cancellation.CancelAfter(TimeSpan.FromSeconds(1));
                    _ = await Assert.That(async () => await OpenCodeServer.StartAsync(silent, cancellation.Token)).Throws<OperationCanceledException>();
                }

                return 0;
            case "exit before readiness":
                _ = await Assert.That(async () => await OpenCodeServer.StartAsync(LadderTree.Options(output, TimeSpan.FromSeconds(5), "exit-before-ready=7"), cancellationToken))
                    .Throws<OpenCodeServerException>();
                return 0;
            case "stdout closed before readiness":
                var closing = LadderTree.Options(output, TimeSpan.FromSeconds(5), "close-stdout");
                closing.ReadinessTimeout = TimeSpan.FromSeconds(5);
                _ = await Assert.That(async () => await OpenCodeServer.StartAsync(closing, cancellationToken)).Throws<OpenCodeServerException>();
                return 0;
            case "non-contract first line":
                silent.Command = ["bun", "-e", reportPid + "console.log('hello'); setInterval(() => {}, 1000);"];
                _ = await Assert.That(async () => await OpenCodeServer.StartAsync(silent, cancellationToken)).Throws<OpenCodeServerException>();
                return 0;
            case "spawn failure":
                // A file that exists but is not executable fails the spawn itself, after its pipes exist.
                var notExecutable = FileSystem.Path.Combine(runRoot.Path, "not-executable");
#if NET
                await FileSystem.File.WriteAllTextAsync(notExecutable, "not a program", cancellationToken);
#else
                FileSystem.File.WriteAllText(notExecutable, "not a program");
#endif
                silent.Command = [notExecutable];
                _ = await Assert.That(async () => await OpenCodeServer.StartAsync(silent, cancellationToken)).Throws<OpenCodeServerException>();
                return 0;
            case "missing working directory":
                silent.WorkingDirectory = FileSystem.Path.Combine(runRoot.Path, "missing");
                _ = await Assert.That(async () => await OpenCodeServer.StartAsync(silent, cancellationToken)).Throws<OpenCodeServerException>();
                return 0;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown start path.");
        }
    }

    /// <summary>Ends the stand-in that reported its pid, when it still runs, and waits for it to be gone.</summary>
    private static async Task EndReportedStandInAsync(string pidFile)
    {
        if (!FileSystem.File.Exists(pidFile))
        {
            return;
        }

#if NET
        var text = await FileSystem.File.ReadAllTextAsync(pidFile, CancellationToken.None);
#else
        var text = FileSystem.File.ReadAllText(pidFile);
#endif
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return;
        }

        // The stand-in is this host's child. waitpid without hanging answers 0 only while it still
        // runs unreaped, so a pid the launcher already reaped, which another process may hold by
        // now, is never signalled.
        if (!OperatingSystem.IsWindows() && PosixInterop.WaitPid(pid, out _, NoHang) != 0)
        {
            return;
        }

        ProcessObservation.KillIfRunning(pid);
        _ = await ProcessObservation.ObserveExitWithinAsync(pid, ObservationBound, CancellationToken.None);
    }

    private static List<string> NewPipes(IReadOnlyDictionary<int, OpenDescriptor> before, IReadOnlyDictionary<int, OpenDescriptor> after) =>
        [
            .. after
                .Where(entry => !before.Values.Any(known => known.SharesPipeWith(entry.Value)))
                .Select(static entry => entry.Key.ToString(CultureInfo.InvariantCulture) + " -> " + entry.Value),
        ];

    private static async Task AssertWindowsConcurrentStartsAsync(CancellationToken cancellationToken)
    {
        var starts = Enumerable.Range(0, Servers).Select(_ => OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.log('" + ReadyLine + "'); setInterval(() => {}, 1000);"],
                GracefulShutdownTimeout = TimeSpan.Zero,
            },
            cancellationToken)).ToArray();
        var pids = new List<int>();
        try
        {
            await Task.WhenAll(starts);
        }
        finally
        {
            foreach (var server in await SucceededAsync(starts))
            {
                pids.Add(server.ProcessId);
                await server.DisposeAsync();
            }
        }

        foreach (var pid in pids)
        {
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(pid, ObservationBound, cancellationToken)).IsTrue();
        }

        Console.WriteLine("branch: Windows — " + Servers.ToString(CultureInfo.InvariantCulture) + " concurrent starts on Process, each ended");
    }
}
