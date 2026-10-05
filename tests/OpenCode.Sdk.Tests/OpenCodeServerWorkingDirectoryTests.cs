using System.Globalization;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// <see cref="OpenCodeServerOptions.WorkingDirectory"/> against real servers. On Linux and macOS the
/// spawn changes the child's directory through the C library's file action, and where the C
/// library lacks that action (glibc before 2.29, injected here) it starts the server through
/// <c>env -C</c>, which keeps the same process. On Windows <c>Process</c> applies the directory,
/// unchanged; each Windows arm proves the same directory through it.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServerWorkingDirectoryTests
{
    private const string ReadyLine = "{\"url\":\"http://127.0.0.1:1\"}";

    /// <summary>Reports its pid and its resolved working directory after the readiness line, then runs until it is ended.</summary>
    private const string StandIn =
        "console.log('" + ReadyLine + "'); console.log('pid=' + process.pid + ' cwd=' + require('node:fs').realpathSync(process.cwd())); setInterval(() => {}, 1000);";

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Start_The_Server_In_Its_Working_Directory(CancellationToken cancellationToken)
    {
        using var runRoot = new TestRunRoot(FileSystem);
        await AssertStartsInDirectoryAsync(runRoot, LauncherSeams.ForCurrentProcess(), cancellationToken);
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Start_The_Server_In_Its_Working_Directory_Through_Env_When_The_C_Library_Cannot(CancellationToken cancellationToken)
    {
        using var runRoot = new TestRunRoot(FileSystem);
        var missing = new MissingWorkingDirectoryAction();
        await AssertStartsInDirectoryAsync(
            runRoot, LauncherSeams.ForCurrentProcess() with { Spawn = new PosixSpawn(missing) }, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(missing.Asked).IsEqualTo(1);
        }
    }

    /// <summary>
    /// A missing directory and a missing executable fail a spawn with the same errno, so the failure
    /// names the directory. Through <c>env -C</c> the start fails instead as an exit with code 125,
    /// and env's own message names the directory in the stderr tail.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Name_A_Working_Directory_That_Does_Not_Exist(CancellationToken cancellationToken)
    {
        using var runRoot = new TestRunRoot(FileSystem);
        var directory = FileSystem.Path.Combine(runRoot.Path, "missing-" + Guid.NewGuid().ToString("N"));
        var options = new OpenCodeServerOptions { Command = ["bun", "-e", StandIn], WorkingDirectory = directory };

        var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(options, cancellationToken)).Throws<OpenCodeServerException>();
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("branch: Windows — Process refused the directory: " + failure!.Message);
            return;
        }

        var trampolined = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(
            options,
            LauncherSeams.ForCurrentProcess() with { Spawn = new PosixSpawn(new MissingWorkingDirectoryAction()) },
            cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains(directory);
        await Assert.That(trampolined!.Message).Contains("exited with code 125");
        await Assert.That(trampolined.Message).Contains(directory);
        Console.WriteLine("branch: POSIX — " + failure.Message + " / " + trampolined.Message);
    }

    private static async Task AssertStartsInDirectoryAsync(TestRunRoot runRoot, LauncherSeams seams, CancellationToken cancellationToken)
    {
        var name = "server-cwd-" + Guid.NewGuid().ToString("N");
        var directory = FileSystem.Path.Combine(runRoot.Path, name);
        _ = FileSystem.Directory.CreateDirectory(directory);
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions { Command = ["bun", "-e", StandIn], WorkingDirectory = directory, Output = output },
            seams,
            cancellationToken);
        string report;
        try
        {
            report = await LiveReadiness.WaitAsync(
                _ => Task.FromResult(output.GetSnapshot().StandardOutput.Skip(1).FirstOrDefault() ?? string.Empty),
                static line => line.Length > 0,
                "the stand-in's directory report",
                cancellationToken);
        }
        finally
        {
            await server.DisposeAsync();
        }

        // The directory resolves through symbolic links (macOS keeps temporary files under /private),
        // so only its unique last segment is compared; the pid shows the process was not replaced.
        await Assert.That(report).StartsWith("pid=" + server.ProcessId.ToString(CultureInfo.InvariantCulture) + " cwd=");
        await Assert.That(report).EndsWith(name);
        Console.WriteLine("branch: " + (OperatingSystem.IsWindows() ? "Windows" : "POSIX") + " — " + report);
    }
}
