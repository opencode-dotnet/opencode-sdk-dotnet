using System.Globalization;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The Windows spawn against real processes, beyond the job: the server inherits no handle of its
/// owner's, a start and its release leave no handle behind, the exit code is reported in full, and a
/// server that logs to its owner shares the owner's console while one that does not gets a console
/// of its own. The proofs that must look at a whole host process run in the service
/// fixture, so no other test's handles or children can blur them. Linux and macOS spawn through
/// <c>posix_spawn</c>, whose descriptor proofs live in the descriptor tests; each POSIX arm says so.
/// Keyless <c>[NotInParallel]</c>: the proofs ride wall-clock bounds.
/// </summary>
[NotInParallel]
public sealed class OpenCodeServerWindowsTests
{
    private const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";

    private const string IdleStandIn = "console.log('" + ReadyLine + "'); setInterval(() => {}, 1000);";

    /// <summary>The access-violation status as a signed 32-bit value, the bits <c>Process.ExitCode</c> gives.</summary>
    private const int AccessViolation = -1073741819;

    private static readonly RealFileSystem FileSystem = new();

    private static readonly IReadOnlyDictionary<string, string?> NoOverlay = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// A host that owns an inheritable pipe while it starts a server sees the pipe's end-of-stream as
    /// soon as it closes its own write end: the server received its three standard handles and no
    /// other. A server that inherited the write end would hold the end-of-stream away for as long as
    /// it runs, which is how a parent capturing the host's output never saw it end.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Hand_The_Server_None_Of_The_Hosts_Inheritable_Handles(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the descriptor tests prove the server receives only its standard descriptors");
            return;
        }

        var result = await new ServiceFixtureCommand(FileSystem).RunAsync(["handle-isolation", "bun", "-e", IdleStandIn], NoOverlay, cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        await Assert.That(result.StandardOutput.Trim()).IsEqualTo("inherited pipe eof=True").Because(result.StandardError);
    }

    /// <summary>
    /// Starts, failed starts, and releases, round after round, in a host of their own: once every
    /// exit watch is released the host's handle count is where it was, so no round leaves a handle.
    /// The first round warms the host up: the thread pool's threads and the runtime's own handles
    /// settle there, and only the slope after it is measured.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task StartAsync_Should_Leave_No_Handle_Behind_Across_Starts_And_Releases(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the descriptor tests prove every path closes the pipes it created");
            return;
        }

        var result = await new ServiceFixtureCommand(FileSystem).RunAsync(["handle-cycles", "bun", "-e", IdleStandIn], NoOverlay, cancellationToken);
        var line = result.StandardOutput.Trim();
        const string prefix = "handles=";
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        await Assert.That(line).StartsWith(prefix).Because(result.StandardError);
        var counts = line[prefix.Length..].Split(',').Select(static count => int.Parse(count.Trim(), CultureInfo.InvariantCulture)).ToArray();

        // Ten rounds of three starts each: a single handle left per start would add thirty.
        await Assert.That(counts[2] - counts[1]).IsLessThanOrEqualTo(5).Because(line);
        await Assert.That(counts[1] - counts[0]).IsLessThanOrEqualTo(10).Because(line);
        BranchReport.Print("Windows — host handle counts after warm-up, one, and two rounds: " + line[prefix.Length..]);
    }

    /// <summary>
    /// A Windows exit code is reported in the full 32 bits the operating system gives: an access
    /// violation reads as its own status, not as its low byte.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Report_The_Full_Exit_Code_Of_A_Server_That_Exits_Before_Readiness(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — an exit code is eight bits there, which the lifecycle tests prove");
            return;
        }

        var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
        var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = [fixture[0], fixture[1], "exit-code", AccessViolation.ToString(CultureInfo.InvariantCulture)] },
            cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("exited with code -1073741819 (0xC0000005) before reporting readiness");
        await Assert.That(failure.Message).Contains("exiting");
    }

    /// <summary>
    /// Windows compares environment names ignoring case: a caller entry spelled in another case than
    /// the host's replaces it, so the environment block the server receives holds one entry with the
    /// caller's value rather than two that differ only in case, of which the server's runtime would
    /// read either.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Replace_A_Host_Variable_Spelled_In_Another_Case(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — names compare case-sensitively there, which the composer tests prove");
            return;
        }

        var seams = LauncherSeams.ForCurrentProcess();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in seams.HostEnvironment)
        {
            host[entry.Key] = entry.Value;
        }

        host["Opencode_Sdk_Test_Cased"] = "host";
        var recording = new EnvironmentRecordingSpawn();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", IdleStandIn],
                Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["OPENCODE_SDK_TEST_CASED"] = "entry" },
            },
            seams with { HostEnvironment = host, WindowsSpawn = recording },
            cancellationToken);
        await server.DisposeAsync();
        var values = recording.Environment!
            .Where(static entry => string.Equals(entry.Key, "OPENCODE_SDK_TEST_CASED", StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.Value)
            .ToArray();

        await Assert.That(values).IsEquivalentTo(["entry"]);
        BranchReport.Print("Windows — the server's environment held one entry for the name: " + values[0]);
    }

    /// <summary>The shipped spawn, recording the environment the launcher asked it to pass.</summary>
    private sealed class EnvironmentRecordingSpawn : IWindowsSpawn
    {
        private readonly WindowsSpawn _spawn = new();

        public IReadOnlyDictionary<string, string>? Environment { get; private set; }

        public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
        {
            Environment = request.Environment;
            return _spawn.Spawn(request);
        }
    }

    /// <summary>
    /// Without <c>OPENCODE_PRINT_LOGS=1</c> the server has a hidden console of its own, as libuv
    /// gives a hidden child whose stdio it pipes. With it, the server shares its owner's console, as
    /// upstream's server does, so a Ctrl+C in that console reaches the server too. The descriptor
    /// tests prove the stderr hand-over itself.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StartAsync_Should_Share_The_Owners_Console_Only_When_The_Owner_Asks_For_Logs(bool printLogs, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the server leads a session of its own there, out of reach of the owner's terminal either way");
            return;
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (printLogs)
        {
            environment["OPENCODE_PRINT_LOGS"] = "1";
        }

        await using var host = await LauncherHost.StartAsync(["bun", "-e", IdleStandIn], environment, LauncherHostIgnores.Nothing, cancellationToken);
        var console = await host.ReadReportAsync(cancellationToken);

        await Assert.That(console).IsEqualTo("server shares console=" + printLogs.ToString(CultureInfo.InvariantCulture)).Because(host.DescribeError());
        BranchReport.Print("Windows — OPENCODE_PRINT_LOGS " + (printLogs ? "set" : "unset") + ": " + console);
    }
}
