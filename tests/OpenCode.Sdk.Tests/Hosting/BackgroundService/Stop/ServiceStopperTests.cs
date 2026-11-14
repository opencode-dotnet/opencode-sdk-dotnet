using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions.Testing;
using TimeProvider = System.TimeProvider;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Stop;

/// <summary>
/// The stop lifecycle over substituted seams: the environment, the filesystem double,
/// and scripted process control. No socket, no real process; the pinned client's <c>stop</c>
/// and <c>terminate</c> decisions only.
/// The poll is real code under accelerated timing; the process control scripts what each look sees.
/// </summary>
public sealed class ServiceStopperTests
{
    private const int RegisteredPid = 48213;

    private const string SidecarSuffix = ".pty-handoff";
    private static readonly string SidecarContent = new FixtureLoader().LoadJson("BackgroundService.pty-handoff-valid.json");
    private static readonly ProcessIdentity Live = new(RegisteredPid, new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));

    /// <summary>The pin's 50 ms × 100 poll, accelerated: each rung looks four times, 2 ms apart.</summary>
    private static readonly ServiceTiming Timing = new(
        RequestTimeout: TimeSpan.FromSeconds(2),
        PollInterval: TimeSpan.FromMilliseconds(1),
        SpawnDelay: TimeSpan.Zero,
        MaxSpawnDelay: TimeSpan.Zero,
        PromiseTimeout: TimeSpan.FromSeconds(120),
        StopPollInterval: TimeSpan.FromMilliseconds(2),
        StopPollAttempts: 3);

    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();
    private readonly IServiceProcessControl _processControl = Substitute.For<IServiceProcessControl>();
    private readonly TimeProvider _clock = TimeProvider.System;

    public ServiceStopperTests()
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(Root("state"));
        _environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(Root("config"));

        // The default script: the registered process is alive until the terminate rung reaches it.
        ProcessLeavesOn(ProcessSignal.Terminate);
    }

    [Test]
    [Arguments("reaped")]
    [Arguments("reused")]
    [Arguments("leaked")]
    [Arguments("cancelled")]
    public async Task TerminateAsync_Should_Respect_Unreaped_Children(string outcome)
    {
        SeedModern();
        var time = new ManualTimeProvider();
        var timing = Timing with { StopPollAttempts = 1 };
        _processControl.TrySnapshot(RegisteredPid).Returns(Live, outcome == "reused" ? Live with { StartTimeUtc = Live.StartTimeUtc.AddHours(1) } : null);
        _processControl.TrySignal(Live, Arg.Any<ProcessSignal>()).Returns(false);
        _processControl.IsUnreapedChild(RegisteredPid).Returns(true);
        var fileSystem = new TestablyServiceFileSystem(_fileSystem);
        var registration = await ServiceRegistrationReader.TryReadAsync(fileSystem, SharedRegistrationPath(), CancellationToken.None);
        using var caller = new CancellationTokenSource();
        var pending = new ServiceTerminator(fileSystem, _processControl, timing, time).TerminateAsync(registration!, SharedRegistrationPath(), caller.Token);
        try
        {
            if (outcome == "reused")
            {
                await Assert.That(await Task.WhenAny(pending, time.WaitForArmedAsync(timing.StopPollInterval, TimeSpan.FromSeconds(10))))
                    .IsSameReferenceAs(pending).Because("a reused identity reads as gone without a peek");
                _ = _processControl.DidNotReceiveWithAnyArgs().IsUnreapedChild(default);
                await AssertSharedRegistrationAbsentAsync();
                return;
            }

            var armed = time.WaitForArmedAsync(timing.StopPollInterval, TimeSpan.FromSeconds(10));
            await Assert.That(await Task.WhenAny(pending, armed)).IsSameReferenceAs(armed).Because("an unreaped child must keep the poll running");
            if (outcome == "leaked")
            {
                time.Advance(timing.StopPollInterval);
                var killPoll = time.WaitForArmedAsync(timing.StopPollInterval, TimeSpan.FromSeconds(10));
                await Assert.That(await Task.WhenAny(pending, killPoll)).IsSameReferenceAs(killPoll);
                time.Advance(timing.StopPollInterval);
                var failure = await Assert.That(pending).Throws<OpenCodeServerException>();
                await Assert.That(failure!.Message).IsEqualTo($"The registered background service (pid 48213) is still running after the kill rung; its registration at '{SharedRegistrationPath()}' was left in place.");
                _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Kill);
                await AssertSharedRegistrationPresentAsync();
            }
            else if (outcome == "cancelled")
            {
                await caller.CancelOnWorkerAsync();
                _ = await Assert.That(pending).Throws<OperationCanceledException>();
                await AssertSharedRegistrationPresentAsync();
            }
            else
            {
                _processControl.IsUnreapedChild(RegisteredPid).Returns(false);
                time.Advance(timing.StopPollInterval);
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
                _ = _processControl.DidNotReceive().TrySignal(Live, ProcessSignal.Kill);
                await AssertSharedRegistrationAbsentAsync();
            }
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TerminateAsync_Should_Poll_Each_Rung_On_The_Injected_Clock(bool killRung)
    {
        SeedModern();
        ProcessNeverLeaves();
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processControl.TrySignal(Live, Arg.Any<ProcessSignal>()).Returns(_ =>
        {
            entered.TrySetResult();
            return true;
        });
        var fileSystem = new TestablyServiceFileSystem(_fileSystem);
        var registration = await ServiceRegistrationReader.TryReadAsync(fileSystem, SharedRegistrationPath(), CancellationToken.None);
        var timing = Timing with { StopPollAttempts = 1, StopPollInterval = TimeSpan.FromSeconds(1) };
        using var caller = new CancellationTokenSource();
        var pending = new ServiceTerminator(fileSystem, _processControl, timing, time)
            .TerminateAsync(registration!, SharedRegistrationPath(), caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await time.WaitForArmedAsync(timing.StopPollInterval, TimeSpan.FromSeconds(10));
            if (killRung)
            {
                time.Advance(timing.StopPollInterval);
                await time.WaitForArmedAsync(timing.StopPollInterval, TimeSpan.FromSeconds(10));
                _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Kill);
            }

            time.Advance(timing.StopPollInterval - TimeSpan.FromTicks(1));
            await Assert.That(pending.IsCompleted).IsFalse();
            if (!killRung)
            {
                _ = _processControl.DidNotReceive().TrySignal(Live, ProcessSignal.Kill);
            }

            // The next look sees the process gone; no real-time wait can decide this outcome.
            ProcessIs(null);
            time.Advance(TimeSpan.FromTicks(1));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await AssertSharedRegistrationAbsentAsync();
            _ = _processControl.Received(killRung ? 1 : 0).TrySignal(Live, ProcessSignal.Kill);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments(ServiceRegistrationData.Malformed)]
    [Arguments(ServiceRegistrationData.ArrayRoot)]
    public async Task StopAsync_Should_Clear_The_Sidecar_And_Signal_Nothing_When_The_Registration_Is_Missing_Or_Corrupt(string? document)
    {
        if (document is not null)
        {
            Seed(SharedRegistrationPath(), document);
        }

        Seed(Sidecar(), SidecarContent);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        await Assert.That(Sidecar()).Satisfies(path => !_fileSystem.File.Exists(path));
        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        if (document is not null)
        {
            await AssertSharedRegistrationPresentAsync();
        }
        else
        {
            await AssertSharedRegistrationAbsentAsync();
        }
    }

    [Test]
    [Arguments("successor")]
    [Arguments("removed")]
    public async Task StopAsync_Should_Signal_Nothing_When_The_Registration_Changes_Hands_Before_The_First_Signal(string outcome)
    {
        SeedModern();
        var handoff = Substitute.For<IServicePtyHandoff>();
        handoff.ClearAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (outcome == "successor")
            {
                Seed(SharedRegistrationPath(), ServiceRegistrationData.DuplicatePidResolved);
            }
            else
            {
                _fileSystem.File.Delete(SharedRegistrationPath());
            }

            return Task.CompletedTask;
        });
        var stopper = new ServiceStopper(
            _environment,
            new TestablyServiceFileSystem(_fileSystem),
            handoff,
            _processControl,
            Timing,
            _clock);

        await stopper.StopAsync(options: null, CancellationToken.None);

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        if (outcome == "successor")
        {
            await Assert.That(ReadText(SharedRegistrationPath())).IsEqualTo(ServiceRegistrationData.DuplicatePidResolved);
        }
        else
        {
            await AssertSharedRegistrationAbsentAsync();
        }
    }

    [Test]
    public async Task StopAsync_Should_Remove_The_Registration_Once_The_Terminate_Rung_Ends_The_Process()
    {
        SeedModern();
        Seed(Sidecar(), SidecarContent);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        _ = _processControl.DidNotReceive().TrySignal(Arg.Any<ProcessIdentity>(), ProcessSignal.Kill);
        await Assert.That(SharedRegistrationPath()).Satisfies(path => !_fileSystem.File.Exists(path));
        await Assert.That(Sidecar()).Satisfies(path => !_fileSystem.File.Exists(path));
    }

    [Test]
    public async Task StopAsync_Should_Escalate_To_Kill_When_The_Process_Survives_The_Terminate_Rung()
    {
        SeedModern();
        ProcessLeavesOn(ProcessSignal.Kill);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        AssertEscalationOrder();
        await AssertSharedRegistrationAbsentAsync();
    }

    [Test]
    public async Task StopAsync_Should_Poll_The_Identity_Through_The_Terminate_Rung_Before_Escalating()
    {
        // One look before the signal, then the pin's schedule: a first look and StopPollAttempts
        // more, for each rung that the process survives.
        SeedModern();
        ProcessLeavesOn(ProcessSignal.Kill);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        // Before the terminate rung: 1. During it: 1 + 3. During the kill rung: 1 (gone at once).
        _ = _processControl.Received(6).TrySnapshot(RegisteredPid);
    }

    [Test]
    public async Task StopAsync_Should_Escalate_By_Pid_When_The_Registration_Disappears_After_The_Terminate_Signal()
    {
        // Only the signalled process tells whether it stopped: a vanished record does not end the
        // ladder.
        SeedModern();
        ProcessLeavesOn(ProcessSignal.Kill);
        _processControl.TrySignal(Live, ProcessSignal.Terminate).Returns(_ =>
        {
            _fileSystem.File.Delete(SharedRegistrationPath());
            return true;
        });

        await Stopper().StopAsync(options: null, CancellationToken.None);

        AssertEscalationOrder();
        await AssertSharedRegistrationAbsentAsync();
    }

    [Test]
    public async Task StopAsync_Should_Escalate_By_Pid_And_Keep_A_Successor_Registered_After_The_Terminate_Signal()
    {
        SeedModern();
        ProcessLeavesOn(ProcessSignal.Kill);
        _processControl.TrySignal(Live, ProcessSignal.Terminate).Returns(_ =>
        {
            // Another service registered under the same file while the old process lingered.
            Seed(SharedRegistrationPath(), ServiceRegistrationData.Minimal);
            return true;
        });

        await Stopper().StopAsync(options: null, CancellationToken.None);

        AssertEscalationOrder();
        await Assert.That(ReadText(SharedRegistrationPath())).IsEqualTo(ServiceRegistrationData.Minimal);
    }

    [Test]
    public async Task StopAsync_Should_Throw_And_Leave_The_Registration_When_The_Process_Survives_The_Kill_Rung()
    {
        SeedModern();
        ProcessNeverLeaves();

        var exception = await Assert
            .That(async () => await Stopper().StopAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).Contains("48213");
        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Kill);
        await Assert.That(SharedRegistrationPath()).Satisfies(_fileSystem.File.Exists);
    }

    [Test]
    public async Task StopAsync_Should_Remove_The_Registration_Without_Signalling_When_The_Process_Is_Gone()
    {
        SeedModern();
        ProcessIs(null);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        await Assert.That(SharedRegistrationPath()).Satisfies(path => !_fileSystem.File.Exists(path));
    }

    [Test]
    public async Task StopAsync_Should_Remove_The_Registration_When_The_Process_Left_Before_The_Signal()
    {
        // The control refuses the send because the process left, or its pid was reused, between
        // the snapshot and the signal; the next look sees it gone and the record goes.
        SeedModern();
        var alive = true;
        _processControl.TrySnapshot(RegisteredPid).Returns(_ => alive ? Live : null);
        _processControl.TrySignal(Live, ProcessSignal.Terminate).Returns(_ =>
        {
            alive = false;
            return false;
        });

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.DidNotReceive().TrySignal(Arg.Any<ProcessIdentity>(), ProcessSignal.Kill);
        await Assert.That(SharedRegistrationPath()).Satisfies(path => !_fileSystem.File.Exists(path));
    }

    [Test]
    public async Task StopAsync_Should_Still_Terminate_When_The_Sidecar_Cannot_Be_Removed()
    {
        // The client's stop only warns when the clear fails: terminal handoff is best-effort and
        // must never keep the old service running.
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryReadAllBytesAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(ServiceRegistrationData.Passwordless));
        fileSystem.TryDelete(Sidecar()).Throws(new UnauthorizedAccessException("The sidecar is locked."));
        fileSystem.TryDelete(SharedRegistrationPath()).Returns(true);
        var stopper = new ServiceStopper(
            _environment,
            fileSystem,
            new ServicePtyHandoff(fileSystem, _clock, new ServicePtyShutdown()),
            _processControl,
            Timing,
            _clock);

        await stopper.StopAsync(options: null, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        _ = fileSystem.Received(1).TryDelete(SharedRegistrationPath());
    }

    [Test]
    public async Task StopAsync_Should_Not_Signal_When_Cancelled_Before_The_First_Signal()
    {
        SeedModern();
        using var cancellation = new CancellationTokenSource();
        _processControl.TrySnapshot(RegisteredPid)
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Live;
            });

        await AssertCancelledStopAsync(cancellation.Token);

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        await AssertSharedRegistrationPresentAsync();
    }

    [Test]
    public async Task StopAsync_Should_Leave_The_Registration_When_Cancelled_After_The_Terminate_Signal()
    {
        SeedModern();
        ProcessNeverLeaves();
        using var cancellation = new CancellationTokenSource();
        _processControl.TrySignal(Live, ProcessSignal.Terminate).Returns(_ =>
        {
            // The caller gives up while the terminate rung's poll is under way.
            cancellation.Cancel();
            return true;
        });

        await AssertCancelledStopAsync(cancellation.Token);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        _ = _processControl.DidNotReceive().TrySignal(Arg.Any<ProcessIdentity>(), ProcessSignal.Kill);
        await Assert.That(SharedRegistrationPath()).Satisfies(_fileSystem.File.Exists);
    }

    [Test]
    public async Task StopAsync_Should_Migrate_A_Legacy_Registration_Before_Stopping_In_Channel_Mode()
    {
        // A dev build registered under the hashed legacy name and nothing under the current one:
        // the copy the CLI would make is what the stop reads, ends, and removes; the donor stays.
        var legacy = _fileSystem.Path.Combine(Root("state"), "opencode", ServiceLegacyFilename.For("dev"));
        Seed(legacy, ServiceRegistrationData.DevPrerelease);

        await Stopper().StopAsync(new OpenCodeServerStopOptions { Channel = "dev" }, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        await Assert.That(SharedRegistrationPath()).Satisfies(path => !_fileSystem.File.Exists(path));
        await Assert.That(legacy).Satisfies(_fileSystem.File.Exists);
    }

    [Test]
    public async Task StopAsync_Should_Stop_A_Direct_Passwordless_Registration_Without_The_Environment()
    {
        // Info.password is optional at the pin: a record without one is still a terminate target.
        var direct = Path("elsewhere", "registration.json");
        Seed(direct, ServiceRegistrationData.Passwordless);

        await Stopper().StopAsync(new OpenCodeServerStopOptions { RegistrationFilePath = direct }, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        await Assert.That(direct).Satisfies(path => !_fileSystem.File.Exists(path));
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task StopAsync_Should_Refuse_Contradictory_Options_Before_Touching_Anything()
    {
        var options = new OpenCodeServerStopOptions { Channel = "dev", RegistrationFilePath = Path("elsewhere", "registration.json") };

        var exception = await Assert
            .That(async () => await Stopper().StopAsync(options, CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task StopAsync_Should_Rethrow_Caller_Cancellation()
    {
        var cancelled = new CancellationToken(canceled: true);

        _ = await Assert
            .That(async () => await Stopper().StopAsync(options: null, cancelled))
            .Throws<OperationCanceledException>();
    }

    private async Task AssertSharedRegistrationAbsentAsync() =>
        await Assert.That(SharedRegistrationPath()).Satisfies(path => !_fileSystem.File.Exists(path));

    private async Task AssertCancelledStopAsync(CancellationToken cancellationToken)
    {
        var exception = await Assert.That(async () => await Stopper().StopAsync(options: null, cancellationToken))
            .Throws<OperationCanceledException>();
        await Assert.That(exception!.CancellationToken).IsEqualTo(cancellationToken);
    }

    private async Task AssertSharedRegistrationPresentAsync() =>
        await Assert.That(SharedRegistrationPath()).Satisfies(_fileSystem.File.Exists);

    private void AssertEscalationOrder() =>
        Received.InOrder(() =>
        {
            _ = _processControl.TrySignal(Live, ProcessSignal.Terminate);
            _ = _processControl.TrySignal(Live, ProcessSignal.Kill);
        });

    private ServiceStopper Stopper() =>
        new(
            _environment,
            new TestablyServiceFileSystem(_fileSystem),
            new ServicePtyHandoff(new TestablyServiceFileSystem(_fileSystem), _clock, new ServicePtyShutdown()),
            _processControl,
            Timing,
            _clock);

    private void ProcessIs(ProcessIdentity? identity) =>
        _processControl.TrySnapshot(RegisteredPid).Returns(identity);

    /// <summary>The registered process is alive until the named rung is sent, then every look sees it gone.</summary>
    private void ProcessLeavesOn(ProcessSignal signal)
    {
        var alive = true;
        _processControl.TrySnapshot(RegisteredPid).Returns(_ => alive ? Live : null);
        _processControl.TrySignal(Live, Arg.Any<ProcessSignal>()).Returns(call =>
        {
            if (call.Arg<ProcessSignal>() == signal)
            {
                alive = false;
            }

            return true;
        });
    }

    /// <summary>The registered process takes every signal and survives every look.</summary>
    private void ProcessNeverLeaves()
    {
        _processControl.TrySnapshot(RegisteredPid).Returns(Live);
        _processControl.TrySignal(Live, Arg.Any<ProcessSignal>()).Returns(true);
    }

    private void SeedModern() =>
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));

    private string SharedRegistrationPath() => _fileSystem.Path.Combine(Root("state"), "opencode", "service.json");

    private string Sidecar() => SharedRegistrationPath() + SidecarSuffix;

    private string ReadText(string path) => _fileSystem.File.ReadAllText(path);

    private void Seed(string path, string content)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        _fileSystem.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private string Root(string name) => Path(name);

    private string Path(params string[] segments) =>
        _fileSystem.Path.Combine([_fileSystem.Path.GetTempPath(), "service-stop", .. segments]);
}
