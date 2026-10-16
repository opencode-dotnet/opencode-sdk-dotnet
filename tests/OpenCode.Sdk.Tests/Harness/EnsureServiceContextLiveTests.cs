using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The boundary the Ensure live tests stand on: an <see cref="EnsureServiceContext"/> leaves no
/// process behind. Proven against the pin's own source-run contenders, because the failure it
/// guards against — a losing contender that is still starting when the elected service is ended —
/// needs real contenders racing a real election.
/// </summary>
/// <remarks>
/// Keyless <c>[NotInParallel]</c>, like the Ensure live tests: the race only shows while the host
/// runs nothing else, and a loaded host would stretch the window this test watches. It is a
/// <see cref="TestCategories.ServiceElection"/> test for the same reason the Ensure live tests are:
/// the gate runs it in a pass of its own.
/// </remarks>
[NotInParallel]
[Category(TestCategories.ServiceElection)]
public sealed class EnsureServiceContextLiveTests
{
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>
    /// How long each contender still running after disposal is watched. A bound on a hang, not the
    /// claim: a contender that took the registration over is elected and never leaves on its own,
    /// so it fails at any bound, while a loaded runner only delays one that is leaving. A 20-second
    /// window failed four times on the three-vCPU macOS runner while another host elected; this
    /// bound stays inside the test's own timeout after an election of up to a minute.
    /// </summary>
    private static readonly TimeSpan TakeoverWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Disposing the context right after a ten-caller election, while the losing contenders are
    /// still starting, leaves no process the election started. A loser still starting when the
    /// elected service ends finds nothing registered and takes the registration over, and a loser
    /// that did is elected and never leaves on its own; so every recorded contender is watched past
    /// the point one would have registered.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task DisposeAsync_Should_Leave_No_Process_The_Election_Started(CancellationToken cancellationToken)
    {
        var context = await EnsureServiceContext.CreateAsync(cancellationToken);
        IReadOnlyList<ProcessMark> started;
        try
        {
            var options = new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = context.RegistrationFile,
                Command = [context.ShimPath, "serve", "--service"],
                Environment = context.Environment,
            };
            _ = await Task.WhenAll(
                Enumerable.Range(0, 10).Select(_ => context.EnsureAsync(options, cancellationToken)));
            started = await context.ReadLiveContendersAsync(cancellationToken);
        }
        finally
        {
            await context.DisposeAsync();
        }

        var live = started.Where(mark => mark.IsRunning(FileSystem)).ToList();
        var exited = await Task.WhenAll(live.Select(mark =>
            ProcessObservation.ObserveExitWithinAsync(mark.ProcessId, TakeoverWindow, cancellationToken)));
        var survivors = live.Where((mark, index) => !exited[index] && mark.IsRunning(FileSystem)).ToList();
        try
        {
            await Assert.That(started).IsNotEmpty();
            await Assert.That(survivors).IsEmpty()
                .Because("a contender still starting at disposal must not outlive the context");
        }
        finally
        {
            foreach (var survivor in survivors)
            {
                ProcessObservation.KillIfRunning(survivor.ProcessId);
            }
        }
    }
}
