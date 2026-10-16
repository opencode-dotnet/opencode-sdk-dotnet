using System.Diagnostics;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Stop;

/// <summary>
/// The stop door's escalation end to end: a registered process that ignores the first rung — the
/// isolated fixture's <c>ignore-sigterm</c> mode, registered under a closed endpoint so no daemon
/// is asked anything — is ended by the second rung and its registration removed. On Unix the
/// terminate rung's bound elapses first; on Windows the first rung is already a hard kill.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServerStopEscalationTests
{
    private const string LingeringPassword = "lingering-p455";
    private static readonly RealFileSystem FileSystem = new();
    private static readonly Uri ClosedEndpoint = new("http://127.0.0.1:1");
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(15);

    /// <summary>The pinned terminate rung: 50 ms × 100 polls, so the kill rung starts after about five seconds.</summary>
    private static readonly TimeSpan TerminateRungFloor = TimeSpan.FromSeconds(4);

    [Test]
    [Timeout(120_000)]
    public async Task StopAsync_Should_Reach_The_Kill_Rung_For_A_Registered_Process_That_Ignores_Terminate(CancellationToken cancellationToken)
    {
        using var root = new TestRunRoot(FileSystem);
        await using var lingering = await ServiceFixtureProcess.StartAsync(FileSystem, "ignore-sigterm", cancellationToken);
        var registration = FileSystem.Path.Combine(root.Path, "service.json");
        Seed(registration, ServiceRegistrationDocument.Compose("lingering", "0.0.0-test", ClosedEndpoint, lingering.ProcessId, LingeringPassword));
        var stopwatch = Stopwatch.StartNew();

        await OpenCodeServer.StopAsync(new OpenCodeServerStopOptions { RegistrationFilePath = registration }, cancellationToken);

        stopwatch.Stop();
        await Assert.That(await lingering.ObserveExitWithinAsync(ExitBound, cancellationToken)).IsTrue();
        await Assert.That(FileSystem.File.Exists(registration)).IsFalse();
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine($"branch: Windows — the terminate rung is a hard kill; stop took {stopwatch.Elapsed}");
            return;
        }

        Console.WriteLine($"branch: Unix — SIGTERM ignored, SIGKILL after the terminate rung's bound; stop took {stopwatch.Elapsed}");
        await Assert.That(stopwatch.Elapsed).IsGreaterThanOrEqualTo(TerminateRungFloor);
    }

    /// <summary>Writes the registration; synchronous, the shape every test leg's Testably asset has.</summary>
    private static void Seed(string path, string content) => FileSystem.File.WriteAllText(path, content);
}
