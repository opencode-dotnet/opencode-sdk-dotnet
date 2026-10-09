using OpenCode.Sdk.Internal;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The persistent simulation host's diagnostic contract, proven over one dedicated real
/// lifecycle through the SDK's own launcher and collector: the readiness line is the only thing
/// on stdout, and the lifecycle milestones reach stderr with their severity, in order. The ladder
/// ends the host before the lease is released (<c>SIGTERM</c> on Linux and macOS, the forced tree
/// kill on Windows), so the host never writes the milestone it would write on stdin's
/// end-of-stream, and the final collection ends at "ready". The shared
/// <see cref="SimulatedDriveServerFixture"/> rechecks only that diagnostics reached stderr at its
/// own teardown (the milestones are evicted over a chatty session); this test is where the
/// profile's routing is proven on its own. No drive controller is attached here: the milestones
/// do not depend on the control bootstrap, and readiness already proves the manifest's backend
/// port is bound.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class PersistentSimulationHostTests
{
    /// <summary>
    /// The test's deadline stays above everything it wraps, so a wedged gate holder or a host
    /// that never reports readiness fails with the gate's or the launcher's own named error, not a
    /// bare timeout: the port gate (15 min), the readiness bound (3 min) and the shutdown grace
    /// (10 s), plus a margin. The test checks the ordering against the live values first.
    /// </summary>
    private const int TimeoutMilliseconds = 20 * 60 * 1000;

    private const string StdinClosed = "persistent simulation host stdin closed";

    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(TimeoutMilliseconds)]
    public async Task Host_Should_Route_Diagnostics_To_Stderr_Around_Readiness_And_Stdin_Eof(
        CancellationToken cancellationToken)
    {
        await Assert.That(TimeSpan.FromMilliseconds(TimeoutMilliseconds)).IsGreaterThan(
            SimulatedServerLaunch.GateTimeout + OwnedServerPolicy.ReadinessTimeout + OwnedServerPolicy.GracefulShutdownTimeout);

        using var runRoot = new TestRunRoot(FileSystem);
        var output = new OpenCodeServerOutput();
        await using (var server = await StartHostAsync(runRoot, output, cancellationToken))
        {
            await Assert.That(server.Endpoint.IsLoopback).IsTrue();
        }

        var snapshot = output.GetSnapshot();
        await Assert.That(snapshot.StandardOutput.Count).IsEqualTo(1);
        await Assert.That(ServerReadyLine.TryParse(snapshot.StandardOutput[0], out _)).IsTrue();

        var starting = RequireIndex(snapshot, "persistent simulation host starting");
        var ready = RequireIndex(snapshot, "persistent simulation host ready");
        await Assert.That(starting).IsLessThan(ready);
        await Assert.That(snapshot.StandardError[starting]).Contains("level=INFO");
        await Assert.That(snapshot.StandardError[ready]).Contains("level=WARN");
        await Assert.That(snapshot.StandardError.Any(static line => line.Contains(StdinClosed, StringComparison.Ordinal))).IsFalse();
        Console.WriteLine(
            "persistent-host-diagnostics (the ladder ended the host before the lease closed): " + snapshot.StandardError[starting] +
            " | " + snapshot.StandardError[ready]);
    }

    /// <summary>
    /// Starts the host under the port gate and releases the gate at readiness: simulation builds
    /// its network layer eagerly at start, so readiness proves the manifest's backend port is bound.
    /// </summary>
    private static async Task<OpenCodeServer> StartHostAsync(
        TestRunRoot runRoot, OpenCodeServerOutput output, CancellationToken cancellationToken)
    {
        using var gate = await MachineLock.AcquireAsync(FileSystem, MachineLock.DrivePorts, SimulatedServerLaunch.GateTimeout);
        var launch = SimulatedServerLaunch.Prepare(FileSystem, runRoot);
        return await launch.StartAsync(output, cancellationToken);
    }

    /// <summary>
    /// The milestone's index in the final stderr snapshot. A missing milestone fails naming
    /// whether the snapshot lost content: truncation that eats promised evidence is a failure
    /// of this proof, never an excuse for it.
    /// </summary>
    private static int RequireIndex(OpenCodeServerOutputSnapshot snapshot, string milestone)
    {
        for (var index = 0; index < snapshot.StandardError.Count; index++)
        {
            if (snapshot.StandardError[index].Contains(milestone, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"The persistent simulation host did not emit '{milestone}' on stderr " +
            $"(stderr truncated: {snapshot.StandardErrorTruncated}).");
    }
}
