using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Contender;
using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.BackgroundService.Contender;

/// <summary>
/// The shipped <see cref="ServiceContenderSpawner"/> against real processes of this machine: the
/// isolated fixture's <c>contender-probe</c> modes, spawned through the seam exactly the way the
/// pinned client's Ensure loop spawns contenders (<c>spawnServiceContender</c>,
/// <c>packages/client/src/service-contender.ts</c>). One test per parity claim: the returned pid
/// is a live process whose ready line names the same pid; a missing executable throws
/// synchronously; argv and the environment overlay cross byte for byte, Unicode included; the
/// contender leads its own session on Unix; on Windows it has no console, starts hidden, and roots
/// its own process group; stdin and stdout are NUL; the retained tail is the
/// final 8 KiB of stderr; <see cref="ServiceContender.Release"/> drops the retention without
/// killing and keeps the pipe draining; and a contender survives its parent's exit. Every test
/// starts a real process. Keyless <c>[NotInParallel]</c> rather than the server-process key:
/// the release-drain proof paces 1024 timer waits that lag under module overlap, and the
/// three-vCPU macOS leg is where that lag bites, the same starvation profile research log
/// Q157/Q172 records for the stop liveness proof.
/// </summary>
[NotInParallel]
public sealed class ServiceContenderSpawnerTests
{
    /// <summary>The contender-probe block filler after its ten-digit number; the same 54 characters the fixture writes, so the tail boundary is checkable byte for byte.</summary>
    private const string BlockFiller = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_-";

    /// <summary>The fixture's default fill: 256 blocks, so the final 8 KiB cuts across numbered block boundaries.</summary>
    private const int DefaultFillBytes = 16 * 1024;

    /// <summary>The release-drain fill: far over the pipe buffer, paced by the fixture, so a drain that stopped would wedge the contender.</summary>
    private const int DrainFillBytes = 4 * 1024 * 1024;

    /// <summary>The fixture's stdout flood; the seam must give the contender NUL stdout for the flood to complete.</summary>
    private const int StdoutFloodBytes = 80 * 1024;

    /// <summary><c>STARTF_USESHOWWINDOW</c>: the startup info's show state applies.</summary>
    private const uint UseShowWindow = 0x00000001;

    /// <summary><c>SW_HIDE</c>.</summary>
    private const short HideWindow = 0;

    /// <summary>Signals 1 through 31 in a <c>/proc</c> mask, where bit n-1 is signal n.</summary>
    private const ulong StandardSignals = 0x7FFF_FFFF;

    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(15);
    /// <summary>The release-drain proof's finish bound: the paced writer needs about eight
    /// seconds unloaded, and sixty covers the timer lag the three-vCPU macOS leg adds under
    /// module overlap while staying far under the test's own two-minute timeout.</summary>
    private static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SurvivalBound = TimeSpan.FromSeconds(1);
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>The shipped spawner over the shipped POSIX spawn; it holds no per-spawn state.</summary>
    private static readonly ServiceContenderSpawner Spawner = new(new PosixSpawn());

    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Return_A_Live_Process_Whose_Ready_Line_Names_The_Same_Pid(CancellationToken cancellationToken)
    {
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "daemon-sleep"));
        try
        {
            await Assert.That(contender.ProcessId).IsGreaterThan(0);
            await Assert.That(ProcessObservation.IsRunning(contender.ProcessId)).IsTrue();
            var stderr = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(contender.Stderr),
                tail => ReadyPid(tail) == contender.ProcessId,
                "the contender's ready line naming the pid the spawner returned",
                cancellationToken);
            var readyPid = ReadyPid(stderr);
            await Assert.That(readyPid).IsNotNull();
            await Assert.That(readyPid!.Value).IsEqualTo(contender.ProcessId);
            await Assert.That(contender.TryGetFailure()).IsNull();

            // Upstream's release never kills: the contender stays alive for the election it was
            // started for, and the retained tail goes with the retention.
            contender.Release();
            await Assert.That(contender.Stderr).IsEqualTo(string.Empty);
            await Assert.That(ProcessObservation.IsRunning(contender.ProcessId)).IsTrue();
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(contender.ProcessId, SurvivalBound, cancellationToken)).IsFalse();
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    public async Task Spawn_Should_Throw_Synchronously_For_A_Missing_Executable()
    {
        var missing = FileSystem.Path.Combine(
            FileSystem.Path.GetTempPath(), "no-such-contender-" + Guid.NewGuid().ToString("N") + ".exe");

        // The action is synchronous on purpose: the failure must throw from Spawn itself, the way
        // the launcher's start does, not surface later on the returned contender.
        var exception = await Assert
            .That(() => Spawner.Start(new IServiceContenderSpawner.ContenderStartInfo(
                new ResolvedExecutable(missing, missing, IsBatchScript: false),
                [],
                new Dictionary<string, string?>(StringComparer.Ordinal))))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).Contains(missing);
        await Assert.That(exception.InnerException).IsTypeOf<Win32Exception>();
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Pass_Arguments_And_Environment_Byte_For_Byte(CancellationToken cancellationToken)
    {
        const string sentinelName = "SDK_CONTENDER_SENTINEL";
        const string emptyName = "SDK_CONTENDER_EMPTY";
        var removedName = OperatingSystem.IsWindows() ? "TEMP" : "PATH";
        var probeArguments = new[]
        {
            "",
            "sp ace",
            "tab\there",
            "quo\"te",
            "a\\\"b",
            "\"sp ace\\\"",
            "trailing-backslash\\",
            "back\\slash\\path",
            "αβγδ-日本語",
            "🚀-astral-plane",
            "e\u0301-combining",
            "trailing-space ",
            sentinelName,
            emptyName,
            removedName,
        };
        var unicodeValue = "Ω-🚀-日本語-é-e\u0301-sp ace";
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [sentinelName] = unicodeValue,
            [emptyName] = "",
            [removedName] = null,
        };
        var command = FixtureCommand();
        using var contender = Spawner.Start(new IServiceContenderSpawner.ContenderStartInfo(
            new ResolvedExecutable("dotnet", command[0], IsBatchScript: false),
            [command[1], "contender-probe", "echo-argv-env", .. probeArguments],
            overlay));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the echo probe to finish", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            var report = ParseEchoReport(contender.Stderr);

            await Assert.That(report.Argv.SequenceEqual(probeArguments, StringComparer.Ordinal)).IsTrue();
            await Assert.That(report.Environment[sentinelName]).IsEqualTo(unicodeValue);
            await Assert.That(report.Environment[emptyName]).IsEqualTo(string.Empty);
            await Assert.That(report.Environment[removedName]).IsNull();
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    /// <summary>
    /// Environment names are case-insensitive on Windows and case-sensitive on Unix, as libuv and
    /// the platforms treat them: an overlay key spelled differently from the host's variable
    /// replaces it on Windows, where two spellings in one block would leave the child to pick
    /// either, and stands beside it on Unix.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Match_Environment_Names_The_Way_The_Platform_Does(CancellationToken cancellationToken)
    {
        const string hostName = "SDK_CONTENDER_CASED";
        const string overlayName = "sdk_contender_CASED";
        Environment.SetEnvironmentVariable(hostName, "host");
        try
        {
            var command = FixtureCommand();
            using var contender = Spawner.Start(new IServiceContenderSpawner.ContenderStartInfo(
                new ResolvedExecutable("dotnet", command[0], IsBatchScript: false),
                [command[1], "contender-probe", "echo-argv-env", hostName, overlayName],
                new Dictionary<string, string?>(StringComparer.Ordinal) { [overlayName] = "overlay" }));
            try
            {
                await Assert.That(await WaitForTheProbeAsync(contender, "the echo probe to finish", cancellationToken)).IsTrue()
                    .Because(contender.Stderr);
                var report = ParseEchoReport(contender.Stderr);

                await Assert.That(report.Environment[hostName]).IsEqualTo(OperatingSystem.IsWindows() ? "overlay" : "host");
                await Assert.That(report.Environment[overlayName]).IsEqualTo("overlay");
            }
            finally
            {
                ProcessObservation.KillIfRunning(contender.ProcessId);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostName, null);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Run_A_Batch_Shim_From_A_Directory_Whose_Name_Has_A_Space(CancellationToken cancellationToken)
    {
        // The npm install puts opencode.cmd under the user profile, and profile names carry
        // spaces: cmd /s strips exactly the first and last quote after /c, so the line has to be
        // the launcher's BatchCommandLine shape, not the MSVCRT quoting of an argv.
        var directory = FileSystem.Path.Combine(
            FileSystem.Path.GetTempPath(), "opencode contender " + Guid.NewGuid().ToString("N"));
        _ = FileSystem.Directory.CreateDirectory(directory);
        try
        {
            var script = await WriteEchoShimAsync(directory, cancellationToken);
            var startInfo = new IServiceContenderSpawner.ContenderStartInfo(
                new ResolvedExecutable("oc", script, IsBatchScript: true),
                ["serve", "--service", "a b"],
                new Dictionary<string, string?>(StringComparer.Ordinal));

            if (!OperatingSystem.IsWindows())
            {
                // cmd.exe exists only on Windows: the seam refuses rather than improvises.
                _ = await Assert.That(() => Spawner.Start(startInfo)).Throws<OpenCodeServerException>();
                Console.WriteLine("branch: Unix — the batch shim '" + script + "' is refused before anything spawns");
                return;
            }

            using var contender = Spawner.Start(startInfo);
            try
            {
                await Assert.That(await WaitForTheProbeAsync(contender, "the batch shim to finish", cancellationToken)).IsTrue()
                    .Because(contender.Stderr);
                await Assert.That(contender.Stderr).Contains("ARGS=[\"serve\" \"--service\" \"a b\"]");
                Console.WriteLine("branch: Windows — the shim under '" + directory + "' ran with its arguments intact");
            }
            finally
            {
                ProcessObservation.KillIfRunning(contender.ProcessId);
            }
        }
        finally
        {
            FileSystem.Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Start_The_Child_With_Default_Signal_State(CancellationToken cancellationToken)
    {
        // libuv starts a Node child with every signal at its default disposition and an empty
        // mask; posix_spawn alone keeps ignored dispositions (the .NET host ignores SIGPIPE) and
        // the calling thread's mask. The shell execs the reporter, which reads its own state: exec
        // keeps the mask and the ignored dispositions, while a shell that forks the reporter and
        // waits is no witness, since dash blocks every signal around that wait. macOS ps has no
        // ignored-signal column: that arm reads the mask.
        if (OperatingSystem.IsWindows())
        {
            using var windows = Spawner.Start(new IServiceContenderSpawner.ContenderStartInfo(
                new ResolvedExecutable("cmd", SystemCommand(), IsBatchScript: false),
                ["/c", "exit", "0"],
                new Dictionary<string, string?>(StringComparer.Ordinal)));
            await Assert.That(await WaitForTheProbeAsync(windows, "cmd to exit", cancellationToken)).IsTrue();
            await Assert.That(windows.TryGetFailure()).IsNull();
            Console.WriteLine("branch: Windows — no POSIX signal state to reset; contender pid " + windows.ProcessId.ToString(CultureInfo.InvariantCulture) + " ran clean");
            return;
        }

        var report = OperatingSystem.IsLinux()
            ? "exec grep -E '^Sig(Ign|Blk):' /proc/self/status 1>&2"
            : "exec ps -o sigmask= -p $$ 1>&2";
        using var contender = Spawner.Start(new IServiceContenderSpawner.ContenderStartInfo(
            new ResolvedExecutable("sh", "/bin/sh", IsBatchScript: false),
            ["-c", report],
            new Dictionary<string, string?>(StringComparer.Ordinal)));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the signal report to finish", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            var state = contender.Stderr;
            if (OperatingSystem.IsLinux())
            {
                // The standard signals, 1 through 31 — the range libuv's reset loop covers. Bits 31
                // and 32 are glibc's internal SIGCANCEL and SIGSETXID, which sigfillset leaves out
                // and glibc's own posix_spawn child ignores by design; the C library manages them.
                await Assert.That(StatusMask(state, "SigIgn:") & StandardSignals).IsEqualTo(0UL).Because(state);
                await Assert.That(StatusMask(state, "SigBlk:")).IsEqualTo(0UL).Because(state);
            }
            else
            {
                await Assert.That(state.Trim()).IsEqualTo("0");
            }

            Console.WriteLine("branch: Unix — the child started with " + state.Replace('\n', ' ').Trim());
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawned_Contender_Should_Lead_Its_Own_Session_On_Unix(CancellationToken cancellationToken)
    {
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "daemon-sleep"));
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows detaches without sessions (DETACHED_PROCESS, no console control events
                // reach the contender); the arm asserts the spawn left a healthy contender for
                // the Unix arm's structure to describe.
                Console.WriteLine("branch: Windows — no session id to read; contender pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture) + " runs");
                await Assert.That(await ProcessObservation.ObserveExitWithinAsync(contender.ProcessId, SurvivalBound, cancellationToken)).IsFalse();
                return;
            }

            var sessionId = GetSessionId(contender.ProcessId);
            await Assert.That(sessionId).IsGreaterThan(0);
            await Assert.That(sessionId).IsEqualTo(contender.ProcessId);
            Console.WriteLine("branch: Unix — contender pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture) + " leads its own session (getsid == pid)");
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    /// <summary>
    /// Upstream spawns the contender detached, which libuv makes <c>DETACHED_PROCESS</c> on
    /// Windows: the contender has no console at all, so no console control event can reach it.
    /// A spawn without that flag gives a console application a console of its own, windowless
    /// under <c>CREATE_NO_WINDOW</c> but attached all the same, which the probe reads as a console
    /// process list with itself in it.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Spawned_Contender_Should_Have_No_Console_On_Windows(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("branch: Unix (" + RuntimeInformation.OSDescription + ") — no console to detach from; the session test proves the Unix detach");
            return;
        }

        using var contender = Spawner.Start(FixtureProbe("contender-probe", "console-report"));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the console report", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            await Assert.That(contender.TryGetFailure()).IsNull();
            var report = ParseConsoleReport(contender.Stderr);

            await Assert.That(report.Attached).IsFalse().Because(contender.Stderr);
            await Assert.That(report.Window).IsFalse().Because(contender.Stderr);
            Console.WriteLine("branch: Windows — contender pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture) + " reported " + contender.Stderr.Trim());
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    /// <summary>
    /// Upstream spawns the contender with <c>windowsHide</c>, which libuv makes
    /// <c>STARTF_USESHOWWINDOW</c> with <c>SW_HIDE</c>: the first window the contender opens
    /// starts hidden. The contender reads its own startup info back.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Spawned_Contender_Should_Start_Hidden_On_Windows(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("branch: Unix (" + RuntimeInformation.OSDescription + ") — windowsHide has no Unix meaning, and libuv ignores it there");
            return;
        }

        using var contender = Spawner.Start(FixtureProbe("contender-probe", "console-report"));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the console report", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            await Assert.That(contender.TryGetFailure()).IsNull();
            var report = ParseConsoleReport(contender.Stderr);

            await Assert.That(report.StartupFlags & UseShowWindow).IsEqualTo(UseShowWindow).Because(contender.Stderr);
            await Assert.That(report.ShowWindow).IsEqualTo(HideWindow).Because(contender.Stderr);
            Console.WriteLine("branch: Windows — contender pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture) + " reported " + contender.Stderr.Trim());
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    /// <summary>
    /// libuv pairs <c>DETACHED_PROCESS</c> with <c>CREATE_NEW_PROCESS_GROUP</c>: the contender
    /// roots its own process group, so a console control event for the group of whichever
    /// process launched it never names it, should it ever share a console. The probe asks a
    /// console of its own making: a <c>CTRL_BREAK_EVENT</c> for the group its pid names must reach
    /// it and must not reach its member in another group.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Spawned_Contender_Should_Root_Its_Own_Process_Group_On_Windows(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("branch: Unix (" + RuntimeInformation.OSDescription + ") — no Windows process group; the session test proves the contender leads its own session and group");
            return;
        }

        int? memberPid = null;
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "group-report"));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the group report", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            await Assert.That(contender.TryGetFailure()).IsNull();
            using var document = JsonDocument.Parse(contender.Stderr);
            var root = document.RootElement;
            memberPid = root.GetProperty("memberPid").GetInt32();

            await Assert.That(root.GetProperty("rootHeard").GetBoolean()).IsTrue().Because(contender.Stderr);
            await Assert.That(root.GetProperty("memberHeard").GetBoolean()).IsFalse().Because(contender.Stderr);
            Console.WriteLine("branch: Windows — contender pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture) + " reported " + contender.Stderr.Trim());
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
            if (memberPid is { } member)
            {
                ProcessObservation.KillIfRunning(member);
            }
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Give_The_Child_Nul_Standard_Streams(CancellationToken cancellationToken)
    {
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "echo-argv-env"));
        try
        {
            // The probe floods stdout before its report: a child whose stdout is a pipe nobody
            // reads blocks inside the flood and never finishes, which this wait turns into a
            // timeout instead of a hang.
            await Assert.That(await WaitForTheProbeAsync(contender, "the echo probe to finish", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            var report = ParseEchoReport(contender.Stderr);

            await Assert.That(report.Stdin).IsEqualTo("eof");
            await Assert.That(report.StdinIsRedirected).IsTrue();
            await Assert.That(report.StdoutIsRedirected).IsTrue();
            await Assert.That(report.StdoutFlushed).IsEqualTo(StdoutFloodBytes);
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    public async Task CreateStderrPipe_Should_Read_Overlapped_And_Hand_The_Child_A_Synchronous_Write_End()
    {
        // .NET 11's Process and libuv make a child's output pipe this way: an anonymous pipe is
        // always synchronous on Windows, and a pending read on one holds a pool thread for as
        // long as the contender keeps stderr open. The witness is the handle's own I/O mode, the
        // query the runtime's SafeFileHandle.IsAsync makes; the bytes and the end-of-stream
        // must still cross.
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("branch: Unix (" + RuntimeInformation.OSDescription + ") — the runtime's socket-backed anonymous pipe, no Windows handle mode to check");
            return;
        }

        var inheritable = new BackgroundServiceInterop.Kernel32.SecurityAttributes
        {
            Length = (uint)Marshal.SizeOf<BackgroundServiceInterop.Kernel32.SecurityAttributes>(),
            SecurityDescriptor = IntPtr.Zero,
            InheritHandle = 1,
        };
        using var readEnd = ServiceContenderSpawner.CreateStderrPipe(ref inheritable, out var rawWriteEnd);
        using var writeEnd = new Microsoft.Win32.SafeHandles.SafeFileHandle(rawWriteEnd, ownsHandle: true);

        await Assert.That(IoMode(readEnd.SafePipeHandle) & SynchronousIo).IsEqualTo(0u);
        await Assert.That(IoMode(writeEnd) & SynchronousIo).IsNotEqualTo(0u);

        var sent = Encoding.UTF8.GetBytes("contender stderr\n");
        using (var writer = FileSystem.FileStream.New(writeEnd, FileAccess.Write, bufferSize: 1, isAsync: false))
        {
#if NET
            await writer.WriteAsync(sent.AsMemory());
#else
            await writer.WriteAsync(sent, 0, sent.Length);
#endif
        }

        using var received = new MemoryStream();
        await readEnd.CopyToAsync(received);

        await Assert.That(Encoding.UTF8.GetString(received.ToArray())).IsEqualTo("contender stderr\n");
        Console.WriteLine("branch: Windows — read end mode 0x" + IoMode(readEnd.SafePipeHandle).ToString("X", CultureInfo.InvariantCulture) + " (overlapped), write end synchronous, bytes and end-of-stream crossed");
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawn_Should_Keep_Only_The_Final_Eight_Kib_Of_Standard_Error(CancellationToken cancellationToken)
    {
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "stderr-fill", DefaultFillBytes.ToString(CultureInfo.InvariantCulture)));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the stderr-fill probe to finish", cancellationToken)).IsTrue()
                .Because(contender.Stderr);
            await Assert.That(contender.TryGetFailure()).IsNull();

            await Assert.That(contender.Stderr).IsEqualTo(ExpectedTail());
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Release_Should_Keep_Draining_Standard_Error_So_The_Contender_Finishes(CancellationToken cancellationToken)
    {
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "stderr-fill", DrainFillBytes.ToString(CultureInfo.InvariantCulture)));
        try
        {
            _ = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(contender.Stderr),
                static tail => tail.Length > 0,
                "the contender's first stderr chunk",
                cancellationToken);

            contender.Release();
            await Assert.That(contender.Stderr).IsEqualTo(string.Empty);

            // Not killed at release, not dead of a closed pipe: alive across a bound far under
            // the time the remaining write still needs, then gone from the process table when the
            // drain carried it — reaped by the contender itself, with nobody polling it.
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(contender.ProcessId, SurvivalBound, cancellationToken)).IsFalse();
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(contender.ProcessId, DrainBound, cancellationToken)).IsTrue();
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task TryGetFailure_Should_Report_A_Nonzero_Exit_With_The_Stderr_Tail(CancellationToken cancellationToken)
    {
        // The upstream contenderFailure oracle: a contender that exits nonzero is reported with its
        // code and its stderr tail, the one diagnostic a caller gets for a failed start. Finished
        // must mean the exit was observed, the way Node's close event does, or the code is lost.
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "no-such-mode"));
        try
        {
            await Assert.That(await WaitForTheProbeAsync(contender, "the usage exit", cancellationToken)).IsTrue();

            var failure = contender.TryGetFailure();
            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Message).Contains("exited with code 2");
            await Assert.That(failure.Message).Contains("Usage: contender-probe");
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task TryGetFailure_Should_Report_A_Signal_Death_On_Unix(CancellationToken cancellationToken)
    {
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "daemon-sleep"));
        try
        {
            _ = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(contender.Stderr),
                tail => ReadyPid(tail) == contender.ProcessId,
                "the daemon-sleep ready line",
                cancellationToken);
            var control = new ServiceProcessControl();
            var identity = control.TrySnapshot(contender.ProcessId);
            await Assert.That(identity).IsNotNull();
            await Assert.That(control.TrySignal(identity!.Value, ProcessSignal.Kill)).IsTrue();
            await Assert.That(await WaitForTheProbeAsync(contender, "the killed contender to finish", cancellationToken)).IsTrue();

            var failure = contender.TryGetFailure();
            await Assert.That(failure).IsNotNull();
            if (OperatingSystem.IsWindows())
            {
                // TerminateProcess is an exit with the code it names, not a signal.
                await Assert.That(failure!.Message).Contains("exited with code");
                Console.WriteLine("branch: Windows — the kill rung reads as a nonzero exit for pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                await Assert.That(failure!.Message).Contains("terminated on signal 9");
                Console.WriteLine("branch: Unix — the kill rung reads as signal 9 for pid " + contender.ProcessId.ToString(CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Released_Contender_Should_Be_Reaped_When_It_Exits_Without_Anyone_Polling(CancellationToken cancellationToken)
    {
        // Release is how the election hands the winner back: nothing polls the contender again. The
        // host that spawned it is the only one that can reap it on Unix, so the contender must do it
        // itself when its process ends, or the pid lingers as a zombie for the host's lifetime.
        using var contender = Spawner.Start(FixtureProbe("contender-probe", "daemon-sleep"));
        try
        {
            _ = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(contender.Stderr),
                tail => ReadyPid(tail) == contender.ProcessId,
                "the daemon-sleep ready line",
                cancellationToken);
            contender.Release();

            var control = new ServiceProcessControl();
            var identity = control.TrySnapshot(contender.ProcessId);
            await Assert.That(identity).IsNotNull();
            await Assert.That(control.TrySignal(identity!.Value, ProcessSignal.Terminate)).IsTrue();

            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(contender.ProcessId, ExitBound, cancellationToken)).IsTrue()
                .Because("a released contender must still be reaped when its process ends");
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task Spawned_Contender_Should_Survive_Its_Parents_Exit(CancellationToken cancellationToken)
    {
        int? daemonPid = null;
        using var contender = Spawner.Start(ShortLivedParent());
        try
        {
            // The parent is the process the spawner detached; the daemon-sleep it launches
            // inherits the parent's stderr pipe, so its ready line names the pid to observe.
            var stderr = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(contender.Stderr),
                static tail => ReadyPid(tail) is not null,
                "the daemon-sleep ready line through the parent's stderr pipe",
                cancellationToken);
            daemonPid = ReadyPid(stderr);
            await Assert.That(daemonPid).IsNotNull();

            // The short-lived parent is gone — reaped by the contender's own exit watch although
            // the daemon it started still holds the stderr pipe, so the pipe never reached its end.
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(contender.ProcessId, ExitBound, cancellationToken)).IsTrue();

            // ...and the contender it started outlives it: alive now, and still alive a bound
            // later, so a parent-death coupling has a window to fire and be caught.
            await Assert.That(ProcessObservation.IsRunning(daemonPid!.Value)).IsTrue();
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(daemonPid.Value, SurvivalBound, cancellationToken)).IsFalse();

            // End it the way the stop door does — the terminate rung, which Windows makes a hard
            // kill of — proving the probe's own signal-exit contract.
            var control = new ServiceProcessControl();
            var identity = control.TrySnapshot(daemonPid.Value);
            await Assert.That(identity).IsNotNull();
            await Assert.That(control.TrySignal(identity!.Value, ProcessSignal.Terminate)).IsTrue();
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(daemonPid.Value, ExitBound, cancellationToken)).IsTrue();
        }
        finally
        {
            ProcessObservation.KillIfRunning(contender.ProcessId);
            if ((daemonPid ?? PidAfter(contender.Stderr, "spawned pid=")) is { } orphan)
            {
                ProcessObservation.KillIfRunning(orphan);
            }
        }
    }

    /// <summary>The fixture process behind one probe: the host executable, then the fixture and the mode.</summary>
    private static IServiceContenderSpawner.ContenderStartInfo FixtureProbe(params string[] modeAndArguments)
    {
        var command = FixtureCommand();
        return new IServiceContenderSpawner.ContenderStartInfo(
            new ResolvedExecutable("dotnet", command[0], IsBatchScript: false),
            [command[1], .. modeAndArguments],
            new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    /// <summary>
    /// The short-lived parent, which starts the fixture's daemon-sleep and exits at once. Windows
    /// runs the fixture's daemon-parent, which hands the daemon its own stderr pipe and names the
    /// daemon's pid before it exits, so a daemon whose ready line never arrives is still released;
    /// a shell cannot stand in there, because a detached cmd has no console for <c>start /b</c> to
    /// share and the daemon would take a console of its own in place of the pipe. Unix runs sh
    /// with a backgrounded command, whose child keeps the pipe and is reparented to init.
    /// </summary>
    private static IServiceContenderSpawner.ContenderStartInfo ShortLivedParent()
    {
        if (OperatingSystem.IsWindows())
        {
            return FixtureProbe("contender-probe", "daemon-parent");
        }

        var command = FixtureCommand();
        return new IServiceContenderSpawner.ContenderStartInfo(
            new ResolvedExecutable("sh", "/bin/sh", IsBatchScript: false),
            ["-c", ShQuote(command[0]) + " " + ShQuote(command[1]) + " contender-probe daemon-sleep &"],
            new Dictionary<string, string?>(StringComparer.Ordinal));
    }

    /// <summary>Reads one hexadecimal signal mask line of <c>/proc/&lt;pid&gt;/status</c>.</summary>
    private static ulong StatusMask(string status, string field)
    {
        var line = status
            .Split('\n')
            .Single(candidate => candidate.StartsWith(field, StringComparison.Ordinal));
        return ulong.Parse(line[field.Length..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>A batch shim that reports the argument line cmd.exe handed it on stderr, the contender's only open stream.</summary>
    private static async Task<string> WriteEchoShimAsync(string directory, CancellationToken cancellationToken)
    {
        var script = FileSystem.Path.Combine(directory, "oc.cmd");
        using var stream = FileSystem.FileStream.New(script, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("@echo off\r\necho ARGS=[%*] 1>&2\r\n"), cancellationToken);
        return script;
    }

    private static string SystemCommand() =>
        FileSystem.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private static string ShQuote(string value) => "'" + string.Join("'\\''", value.Split('\'')) + "'";

    private static IReadOnlyList<string> FixtureCommand() => new ServiceFixtureCommand(FileSystem).Resolve();

    /// <summary>Waits for the contender's stderr pipe to reach end-of-stream: the probe finished and nothing holds the write end.</summary>
    private static Task<bool> WaitForTheProbeAsync(ServiceContender contender, string description, CancellationToken cancellationToken) =>
        LiveReadiness.WaitAsync(
            _ => Task.FromResult(contender.Finished),
            static finished => finished,
            description,
            cancellationToken);

    /// <summary>Reads the <c>ready pid=&lt;pid&gt;</c> line the daemon-sleep probe prints; null until the line arrives.</summary>
    private static int? ReadyPid(string standardError) => PidAfter(standardError, "ready pid=");

    /// <summary>Reads the pid after the first <paramref name="prefix"/>; null until a line carrying it arrives.</summary>
    private static int? PidAfter(string standardError, string prefix)
    {
        var start = standardError.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += prefix.Length;
        var end = start;
        while (end < standardError.Length && char.IsAsciiDigit(standardError[end]))
        {
            end++;
        }

        if (end == start)
        {
            return null;
        }

        // Digit by digit, the way ServiceFixtureOutput reads its elapsed line: the span-based
        // parse the modern targets prefer does not exist on net472, and Substring draws the
        // reverse complaint there.
        var pid = 0;
        for (var index = start; index < end; index++)
        {
            var digit = standardError[index] - '0';
            if (digit is < 0 or > 9)
            {
                return null;
            }

            pid = checked((pid * 10) + digit);
        }

        return pid;
    }

    private static EchoReport ParseEchoReport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var argv = root.GetProperty("argv")
            .EnumerateArray()
            .Select(static element => element.GetString() ?? string.Empty)
            .ToArray();
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("env").EnumerateObject())
        {
            environment[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
        }

        return new EchoReport(
            argv,
            environment,
            root.GetProperty("stdin").GetString() ?? string.Empty,
            root.GetProperty("stdinIsRedirected").GetBoolean(),
            root.GetProperty("stdoutIsRedirected").GetBoolean(),
            root.GetProperty("stdoutFlushed").GetInt32());
    }

    private static ConsoleReport ParseConsoleReport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new ConsoleReport(
            root.GetProperty("attached").GetBoolean(),
            root.GetProperty("window").GetBoolean(),
            root.GetProperty("startupFlags").GetUInt32(),
            root.GetProperty("showWindow").GetInt16());
    }

    /// <summary>The final 8 KiB of the fixture's default fill: blocks 128 through 255 of 256.</summary>
    private static string ExpectedTail() => string.Concat(
        Enumerable.Range(128, 128).Select(static block => block.ToString("D10", CultureInfo.InvariantCulture) + BlockFiller));

    /// <summary><c>FILE_SYNCHRONOUS_IO_ALERT | FILE_SYNCHRONOUS_IO_NONALERT</c>: either bit makes a handle synchronous.</summary>
    private const uint SynchronousIo = 0x30;

    /// <summary><c>FileModeInformation</c>, the class <c>NtQueryInformationFile</c> answers a handle's I/O mode for.</summary>
    private const int FileModeInformation = 16;

    /// <summary>The handle's I/O mode, the question the runtime's <c>SafeFileHandle.IsAsync</c> asks.</summary>
    private static uint IoMode(SafeHandle handle)
    {
        var status = QueryInformationFile(handle, out _, out var mode, sizeof(uint), FileModeInformation);
        if (status != 0)
        {
            throw new Win32Exception("NtQueryInformationFile failed with NTSTATUS 0x" + status.ToString("X8", CultureInfo.InvariantCulture));
        }

        return mode;
    }

    [DllImport("ntdll", EntryPoint = "NtQueryInformationFile")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int QueryInformationFile(SafeHandle handle, out IoStatusBlock status, out uint mode, uint length, int informationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    /// <summary><c>getsid(2)</c> through the portable <c>libc</c> spelling the SDK's own interop uses.</summary>
    [DllImport("libc", EntryPoint = "getsid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetSessionId(int processId);

    private sealed record ConsoleReport(bool Attached, bool Window, uint StartupFlags, short ShowWindow);

    private sealed record EchoReport(
        string[] Argv,
        Dictionary<string, string?> Environment,
        string Stdin,
        bool StdinIsRedirected,
        bool StdoutIsRedirected,
        int StdoutFlushed);
}
