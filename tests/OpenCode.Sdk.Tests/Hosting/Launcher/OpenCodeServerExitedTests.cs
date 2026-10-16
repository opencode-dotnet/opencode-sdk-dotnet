using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>
/// <see cref="OpenCodeServer.Exited"/> against the <c>ladder-tree.js</c> stand-in: it completes
/// when the server's exit is observed and its output has ended, or one second after the exit when
/// a descendant holds the output open, as upstream's <c>exited</c> does; it drains on its own after
/// a natural exit; and it reports the end disposal gave. The bound runs on the test's clock, which
/// moves only when the test moves it, so the clock decides no verdict by itself. A root that exits
/// while a descendant holds its output exits on a marker file the test writes once the start has
/// returned: an exit that beat the readiness line would send the start into its own drain on the
/// test's clock, which nothing moves while the start is pending.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServerExitedTests
{
    /// <summary>The code the stand-in exits with on its own, distinct from any code an ending gives.</summary>
    private const int NaturalExitCode = 7;

    /// <summary>The bound upstream gives a descendant holding the output after the exit.</summary>
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan JustBeforeTheBound = CloseBound - TimeSpan.FromMilliseconds(1);

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>Bounds an exit or a timer that never comes; it decides nothing else.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private static readonly RealFileSystem FileSystem = new();

    private static readonly string NaturalExit = "exit-after-ready=" + NaturalExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string ExitOnMarker = "exit-on-marker=" + NaturalExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The server exits on its own and its output ends with it, so <c>Exited</c> completes with the
    /// code although the clock never moves: nothing waited for the bound.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Exited_Should_Complete_At_The_Close_When_The_Output_Ends_With_The_Exit(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        var output = new OpenCodeServerOutput();
        OpenCodeServer? server = null;
        try
        {
            server = await OpenCodeServer.StartWithSeamsAsync(
                LadderTree.Options(output, Grace, NaturalExit),
                LauncherSeams.ForCurrentProcess() with { Time = time },
                cancellationToken);

            var exit = await server.Exited.WaitAsync(HangGuard, cancellationToken);

            await Assert.That(exit).IsEqualTo(OpenCodeServerExit.FromExitCode(NaturalExitCode));
        }
        finally
        {
            await EndAsync(server, output);
        }
    }

    /// <summary>
    /// The server exits on its own while a detached descendant holds its stdout, so the output never
    /// ends: <c>Exited</c> starts the bound itself at the exit, without any disposal, and completes
    /// once the bound passes and not a moment before.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Exited_Should_Complete_One_Second_After_A_Natural_Exit_When_A_Descendant_Holds_The_Output(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        var output = new OpenCodeServerOutput();
        using var runRoot = new TestRunRoot(FileSystem);
        var marker = FileSystem.Path.Combine(runRoot.Path, "exit");
        OpenCodeServer? server = null;
        try
        {
            server = await OpenCodeServer.StartWithSeamsAsync(
                LadderTree.OptionsWithMarker(output, Grace, marker, "hold-stdout", ExitOnMarker),
                LauncherSeams.ForCurrentProcess() with { Time = time },
                cancellationToken);
#if NET
            await FileSystem.File.WriteAllTextAsync(marker, string.Empty, cancellationToken);
#else
            FileSystem.File.WriteAllText(marker, string.Empty);
#endif
            var rootExit = await server.ChildExited.WaitAsync(HangGuard, cancellationToken);
            await time.WaitForArmedAsync(CloseBound, HangGuard);

            time.Advance(JustBeforeTheBound);
            var completedBeforeTheBound = server.Exited.IsCompleted;
            time.Advance(CloseBound - JustBeforeTheBound);
            var exit = await server.Exited.WaitAsync(HangGuard, cancellationToken);

            await Assert.That(rootExit.ExitCode).IsEqualTo(NaturalExitCode);
            await Assert.That(completedBeforeTheBound).IsFalse();
            await Assert.That(exit).IsEqualTo(OpenCodeServerExit.FromExitCode(NaturalExitCode));
        }
        finally
        {
            await EndAsync(server, output);
        }
    }

    /// <summary>
    /// The server exits on its own while a detached descendant holds its stderr, so stdout ends with
    /// the exit and stderr does not: <c>Exited</c> waits for both pipes, and once the holder is gone
    /// it completes without the clock moving. A close that waited for stdout alone would complete
    /// while stderr is still held.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Exited_Should_Wait_For_Stderr_When_A_Descendant_Holds_It(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        var output = new OpenCodeServerOutput();
        using var runRoot = new TestRunRoot(FileSystem);
        var marker = FileSystem.Path.Combine(runRoot.Path, "exit");
        OpenCodeServer? server = null;
        try
        {
            server = await OpenCodeServer.StartWithSeamsAsync(
                LadderTree.OptionsWithMarker(output, Grace, marker, "hold-stderr", ExitOnMarker),
                LauncherSeams.ForCurrentProcess() with { Time = time },
                cancellationToken);
#if NET
            await FileSystem.File.WriteAllTextAsync(marker, string.Empty, cancellationToken);
#else
            FileSystem.File.WriteAllText(marker, string.Empty);
#endif
            _ = await server.ChildExited.WaitAsync(HangGuard, cancellationToken);

            // The bound starts while stderr is held; a close that ignored stderr completes instead.
            _ = await Task.WhenAny(time.WaitForArmedAsync(CloseBound, HangGuard), server.Exited).WaitAsync(HangGuard, cancellationToken);
            var completedWhileHeld = server.Exited.IsCompleted;
            ProcessObservation.KillIfRunning(LadderTree.Pid(output, LadderTree.StderrHolder));
            var exit = await server.Exited.WaitAsync(HangGuard, cancellationToken);

            await Assert.That(completedWhileHeld).IsFalse();
            await Assert.That(exit).IsEqualTo(OpenCodeServerExit.FromExitCode(NaturalExitCode));
        }
        finally
        {
            await EndAsync(server, output);
        }
    }

    /// <summary>
    /// Disposal ends a running server, and <c>Exited</c> reports how: on Linux and macOS the
    /// stand-in dies on the group's <c>SIGTERM</c>, which reads as the signal's name and number; on
    /// Windows the tree kill ends it with code 1.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Exited_Should_Report_The_End_Disposal_Gave(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        OpenCodeServer? server = null;
        try
        {
            server = await OpenCodeServer.StartAsync(LadderTree.Options(output, Grace), cancellationToken);
            await server.DisposeAsync();

            var exit = await server.Exited.WaitAsync(HangGuard, cancellationToken);

            var expected = OperatingSystem.IsWindows()
                ? OpenCodeServerExit.FromExitCode(1)
                : OpenCodeServerExit.FromSignal("SIGTERM", 15);
            await Assert.That(exit).IsEqualTo(expected);
            Console.WriteLine("branch: " + (OperatingSystem.IsWindows() ? "Windows" : "POSIX") + " — disposal: the server " + exit);
        }
        finally
        {
            await EndAsync(server, output);
        }
    }

    /// <summary>
    /// Ends every process the stand-in reported before disposing, so a descendant holding the
    /// output is gone and disposal's drain, on the test's clock, has nothing to wait for.
    /// </summary>
    private static async Task EndAsync(OpenCodeServer? server, OpenCodeServerOutput output)
    {
        var survivors = await LadderTree.EndEveryReportedProcessAsync(output);
        if (server is not null)
        {
            await server.DisposeAsync();
        }

        await Assert.That(survivors).IsEmpty();
    }
}
