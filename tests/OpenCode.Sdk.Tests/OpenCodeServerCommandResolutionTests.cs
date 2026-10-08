using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Real-process evidence for how the launcher turns <c>Command[0]</c> into something spawnable.
/// The Windows legs run the npm arrangement that the shipped default actually meets there — a
/// batch shim on PATH and no executable of that name anywhere — and every leg carries the
/// server-process key because a shim mutates the process PATH.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServerCommandResolutionTests
{
    private const string ShimName = "fakeopencode";
    private const string MetacharacterArgument = "serve&calc";

    private static readonly RealFileSystem FileSystem = new();

    /// <summary>The bound the launched tree has to be gone inside, observed by the test itself.</summary>
    private static readonly TimeSpan TerminationBound = TimeSpan.FromSeconds(10);

    private static bool IsWindows =>
#if NET
        OperatingSystem.IsWindows();
#else
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);
#endif

    [Test]
    [Timeout(240_000)]
    public async Task StartAsync_Should_Start_An_Npm_Style_Batch_Shim_Resolved_From_PATH(
        CancellationToken cancellationToken)
    {
        var launch = new PinnedServerLaunch(FileSystem);
        if (!IsWindows)
        {
            // The Unix dialect this platform can reach: npm links its bin entry straight to the
            // real binary, so a bare name resolves with no extension appended and never reaches a
            // command interpreter. Asserted against the real process environment, which is the
            // half the injected-predicate unit tests cannot cover.
            var resolved = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess())
                .Resolve(launch.Command[0]);

            await Assert.That(resolved.IsBatchScript).IsFalse();
            await Assert.That(resolved.Path).IsNotEqualTo(launch.Command[0]);
            await Assert.That(FileSystem.File.Exists(resolved.Path)).IsTrue();
            return;
        }

        // The pinned command ends in its own 'serve' verb; this test supplies that verb through
        // the launcher's Command instead, so the shim forwards everything before it and the
        // batch file's %* carries 'serve --stdio --port 0' through cmd.exe to bun.
        var forwarded = launch.Command.Take(launch.Command.Count - 1);
        using var shim = PathCommandShim.ForwardingTo(FileSystem, ShimName + ".cmd", forwarded);
        using var runRoot = new TestRunRoot(FileSystem);
        var server = await launch.StartAsync(runRoot, [ShimName, "serve"], cancellationToken: cancellationToken);

        int serverPid;
        try
        {
            await Assert.That(server.Endpoint.IsLoopback).IsTrue();
            await Assert.That(server.Endpoint.Port).IsNotEqualTo(0);
            await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsTrue();

            using var client = server.CreateClient();
            var health = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

            await Assert.That(health.Status).IsEqualTo(200);

            // The documented consequence of routing a batch shim through cmd.exe: the owned root
            // is the interpreter, and the server answering health is its grandchild.
            serverPid = checked((int)health.ServerInfo.Pid);
            await Assert.That(serverPid).IsNotEqualTo(server.ProcessId);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }

        await server.DisposeAsync();

        await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsFalse();
        var grandchildTerminated = await ProcessObservation.ObserveExitWithinAsync(
            serverPid, TerminationBound, cancellationToken);
        await Assert.That(grandchildTerminated).IsTrue();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Refuse_A_Cmd_Metacharacter_In_A_Leading_Argument(
        CancellationToken cancellationToken)
    {
        if (!IsWindows)
        {
            // cmd.exe re-parsing is a Windows fact, so nothing is refused here: the same argument
            // rides straight through and the only failure left is the unresolvable name itself.
            var passedThrough = await Assert.That(async () => await OpenCodeServer.StartAsync(
                new OpenCodeServerOptions { Command = [ShimName + ".cmd", MetacharacterArgument] },
                cancellationToken)).Throws<OpenCodeServerException>();

            await Assert.That(passedThrough!.Message).Contains("was not found on PATH");
            return;
        }

        using var shim = PathCommandShim.RecordingInvocation(FileSystem, ShimName + ".cmd");

        var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = [ShimName, MetacharacterArgument] },
            cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains(MetacharacterArgument);
        await Assert.That(failure.Message).Contains("cmd.exe");

        // The refusal is fail-closed, not a late check: the shim records every run it gets, and
        // it never got one.
        await Assert.That(FileSystem.File.Exists(shim.MarkerPath)).IsFalse();
    }
}
