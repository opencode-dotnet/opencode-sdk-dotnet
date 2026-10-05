using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Posix;

/// <summary>
/// The exit watch against scripted seams, with no process: the status a reap decodes, the unknown
/// status of a wait that fails or throws, the pid fallback that signals only an unreaped child
/// (asked of <c>waitpid</c> without hanging, so a child something else reaped is never signalled),
/// and the probe the watch falls back to when the kernel reaps children itself.
/// </summary>
public sealed class PosixChildExitWatchTests
{
    private const int Pid = 4242;

    private static readonly TimeSpan ProbeBound = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Exited_Should_Report_The_Status_The_Reap_Decoded()
    {
        using var exits = new ScriptedChildExit();
        var watch = PosixChildExitWatch.Start(Pid, exits, Substitute.For<IProcessGroupSignal>(), childrenReapedAutomatically: false);

        exits.Exit(new ChildExitStatus { ExitCode = 7 });

        await Assert.That(await ChildExitObservation.WithinAsync(watch, ProbeBound)).IsEqualTo(new ChildExitStatus { ExitCode = 7 });
    }

    [Test]
    public async Task Exited_Should_Be_Unknown_When_The_Wait_Fails()
    {
        using var exits = new ScriptedChildExit();
        var signals = Substitute.For<IProcessGroupSignal>();
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);

        exits.Release();

        await Assert.That(await ChildExitObservation.WithinAsync(watch, ProbeBound)).IsEqualTo(ChildExitStatus.Unknown);
        await Assert.That(watch.SignalRootWhileUnreaped(ProcessSignal.Terminate)).IsEqualTo(SignalDelivery.NoSuchTarget);
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
    }

    /// <summary>An exception on the watch thread would end the host; the watch reports it as an unknown exit instead.</summary>
    [Test]
    public async Task Exited_Should_Be_Unknown_When_The_Wait_Throws()
    {
        using var exits = new ScriptedChildExit { WaitFailure = new EntryPointNotFoundException("waitid") };
        var signals = Substitute.For<IProcessGroupSignal>();
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);

        exits.Release();

        await Assert.That(await ChildExitObservation.WithinAsync(watch, ProbeBound)).IsEqualTo(ChildExitStatus.Unknown);
        await Assert.That(watch.SignalRootWhileUnreaped(ProcessSignal.Kill)).IsEqualTo(SignalDelivery.NoSuchTarget);
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
    }

    [Test]
    public async Task SignalRootWhileUnreaped_Should_Signal_The_Pid_Until_The_Reap()
    {
        using var exits = new ScriptedChildExit();
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.SignalProcess(Pid, ProcessSignal.Terminate).Returns(SignalDelivery.Delivered);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);

        var before = watch.SignalRootWhileUnreaped(ProcessSignal.Terminate);
        exits.Exit(new ChildExitStatus { Signal = 15 });
        _ = await ChildExitObservation.WithinAsync(watch, ProbeBound);
        var after = watch.SignalRootWhileUnreaped(ProcessSignal.Terminate);

        await Assert.That(before).IsEqualTo(SignalDelivery.Delivered);
        await Assert.That(after).IsEqualTo(SignalDelivery.NoSuchTarget);
        _ = signals.Received(1).SignalProcess(Pid, ProcessSignal.Terminate);
    }

    /// <summary>
    /// With children reaped by the kernel, a blocked wait could sleep until every child of the host
    /// is gone, so the watch never enters it: it probes, and a pid that no longer exists is an exit
    /// whose status nobody could read.
    /// </summary>
    [Test]
    public async Task Exited_Should_Be_Probed_When_The_Kernel_Reaps_Children_Itself()
    {
        using var exits = new ScriptedChildExit(reapedAutomatically: true);
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.ProbeProcess(Pid).Returns(SignalDelivery.Delivered, SignalDelivery.Delivered, SignalDelivery.NoSuchTarget);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, exits.AreChildrenReapedAutomatically());

        var status = await ChildExitObservation.WithinAsync(watch, ProbeBound);

        await Assert.That(status).IsEqualTo(ChildExitStatus.Unknown);
        await Assert.That(exits.Waits).IsEqualTo(0);
        _ = signals.Received(3).ProbeProcess(Pid);
    }

    /// <summary>
    /// An ignored <c>SIGCHLD</c> inherited on macOS leaves a zombie no one reaps, which a probe alone
    /// would report alive for ever; the reap that does not hang takes it, with its status.
    /// </summary>
    [Test]
    public async Task Exited_Should_Reap_A_Zombie_The_Probe_Still_Finds()
    {
        var exits = Substitute.For<IChildExitStatus>();
        _ = exits.Reap(Pid).Returns(null, new ChildExitStatus { ExitCode = 0 });
        _ = exits.WaitUntilExited(Pid).Throws(new InvalidOperationException("The probe never waits."));
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.ProbeProcess(Pid).Returns(SignalDelivery.Delivered);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: true);

        var status = await ChildExitObservation.WithinAsync(watch, ProbeBound);

        await Assert.That(status).IsEqualTo(new ChildExitStatus { ExitCode = 0 });
        _ = exits.DidNotReceive().WaitUntilExited(Arg.Any<int>());
    }

    /// <summary>
    /// The kernel (an ignored <c>SIGCHLD</c> on Linux) or a runtime that reaps every child (the
    /// .NET runtime as pid 1) took the child before the watch did, so its pid may already name an
    /// unrelated process, which a <c>kill(pid, 0)</c> would still find. <c>waitpid</c> reports the
    /// pid as no child of this process, and nothing is sent.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SignalRootWhileUnreaped_Should_Not_Signal_A_Pid_That_Is_No_Child_Any_More(bool childrenReapedAutomatically)
    {
        using var waitReleased = new ManualResetEventSlim();
        var exits = BlockedWait(waitReleased, ChildExitStatus.Unknown, Environment.CurrentManagedThreadId);
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.ProbeProcess(Pid).Returns(SignalDelivery.Delivered);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically);
        SignalDelivery delivery;
        ChildExitStatus status;
        try
        {
            delivery = watch.SignalRootWhileUnreaped(ProcessSignal.Terminate);
        }
        finally
        {
            waitReleased.Set();
            status = await ChildExitObservation.WithinAsync(watch, ProbeBound);
        }

        await Assert.That(delivery).IsEqualTo(SignalDelivery.NoSuchTarget);
        await Assert.That(status).IsEqualTo(ChildExitStatus.Unknown);
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
    }

    /// <summary>
    /// The child had exited and nobody had reaped it yet: the signal path's <c>waitpid</c> reaps it,
    /// sends nothing, and the status it read is the exit the watch reports, not an unknown one.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SignalRootWhileUnreaped_Should_Report_The_Status_It_Reaped_Instead_Of_Signalling(bool childrenReapedAutomatically)
    {
        var exited = new ChildExitStatus { ExitCode = 5 };
        using var waitReleased = new ManualResetEventSlim();
        var exits = BlockedWait(waitReleased, exited, Environment.CurrentManagedThreadId);
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.ProbeProcess(Pid).Returns(SignalDelivery.Delivered);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically);
        SignalDelivery delivery;
        ChildExitStatus status;
        try
        {
            delivery = watch.SignalRootWhileUnreaped(ProcessSignal.Kill);
        }
        finally
        {
            waitReleased.Set();
            status = await ChildExitObservation.WithinAsync(watch, ProbeBound);
        }

        await Assert.That(delivery).IsEqualTo(SignalDelivery.NoSuchTarget);
        await Assert.That(status).IsEqualTo(exited);
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
    }

    /// <summary>With the kernel reaping children, a child <c>waitpid</c> still finds running is signalled.</summary>
    [Test]
    public async Task SignalRootWhileUnreaped_Should_Signal_A_Running_Child_When_The_Kernel_Reaps_Children_Itself()
    {
        using var gone = new ManualResetEventSlim();
        var exits = Substitute.For<IChildExitStatus>();
        _ = exits.Reap(Pid).Returns((ChildExitStatus?)null);
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.ProbeProcess(Pid).Returns(_ => gone.IsSet ? SignalDelivery.NoSuchTarget : SignalDelivery.Delivered);
        _ = signals.SignalProcess(Pid, ProcessSignal.Terminate).Returns(SignalDelivery.Delivered);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: true);
        SignalDelivery delivery;
        try
        {
            delivery = watch.SignalRootWhileUnreaped(ProcessSignal.Terminate);
        }
        finally
        {
            gone.Set();
            _ = await ChildExitObservation.WithinAsync(watch, ProbeBound);
        }

        await Assert.That(delivery).IsEqualTo(SignalDelivery.Delivered);
        _ = signals.Received(1).SignalProcess(Pid, ProcessSignal.Terminate);
    }

    /// <summary>
    /// An exit seam whose blocking wait returns only once <paramref name="waitReleased"/> is set,
    /// failing as a wait for a pid that is no child any more fails. Its reap without hanging answers
    /// <paramref name="signalPathReap"/> only to the first call from <paramref name="signalPathThread"/>,
    /// the thread that runs the signal path; until then any other caller (the probe thread) finds the
    /// child running, and afterwards every caller finds it gone. Only the signal path can therefore
    /// read that status.
    /// </summary>
    private static IChildExitStatus BlockedWait(ManualResetEventSlim waitReleased, ChildExitStatus signalPathReap, int signalPathThread)
    {
        var exits = Substitute.For<IChildExitStatus>();
        _ = exits.WaitUntilExited(Pid).Returns(_ =>
        {
            waitReleased.Wait();
            return false;
        });
        var reaped = 0;
        _ = exits.Reap(Pid).Returns(_ =>
        {
            if (Environment.CurrentManagedThreadId == signalPathThread && Interlocked.Exchange(ref reaped, 1) == 0)
            {
                return signalPathReap;
            }

            return Volatile.Read(ref reaped) == 1 ? ChildExitStatus.Unknown : null;
        });
        return exits;
    }
}
