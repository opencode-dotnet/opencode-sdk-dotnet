using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.Handoff;
using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.BackgroundService.Stop;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.BackgroundService.Registration;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.BackgroundService.Stop;

/// <summary>
/// The stop lifecycle over substituted seams: the environment, the filesystem double, a scripted
/// probe, a scripted persistent-terminal shutdown, and scripted process control. No socket, no
/// real process; the pinned client's <c>stop</c> and <c>terminate</c> decisions only (design §13.3).
/// The poll is real code under accelerated timing; the process control scripts what each look sees.
/// </summary>
public sealed class ServiceStopperTests
{
    private const int RegisteredPid = 48213;
    private const string Version = "2.0.3";
    private const string SidecarSuffix = ".pty-handoff";
    private static readonly string SidecarContent = new FixtureLoader().LoadJson("BackgroundService.pty-handoff-valid.json");
    private static readonly ProcessIdentity Live = new(RegisteredPid, new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc));
    private static readonly ServiceProbeResult NoService = new(State: null, Version: null, TimedOut: false, Compatible: true);

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
    private readonly IServiceInfoProbe _probe = Substitute.For<IServiceInfoProbe>();
    private readonly IServicePtyShutdown _ptyShutdown = Substitute.For<IServicePtyShutdown>();
    private readonly IServiceProcessControl _processControl = Substitute.For<IServiceProcessControl>();
    private readonly IServiceClock _clock = Substitute.For<IServiceClock>();

    public ServiceStopperTests()
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(Root("state"));
        _environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(Root("config"));

        // The default script: no usable daemon answers, and the registered process is alive until
        // the terminate rung reaches it.
        Answer(NoService);
        ProcessLeavesOn(ProcessSignal.Terminate);
    }

    [Test]
    public async Task StopAsync_Should_Complete_And_Clear_The_Sidecar_When_The_Registration_Is_Missing()
    {
        Seed(Sidecar(), SidecarContent);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(Sidecar())).IsFalse();
        _ = _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
        _ = _ptyShutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
    }

    [Test]
    [Arguments(ServiceRegistrationData.Malformed)]
    [Arguments(ServiceRegistrationData.ArrayRoot)]
    public async Task StopAsync_Should_Leave_A_Corrupt_Registration_Alone_And_Clear_The_Sidecar(string document)
    {
        Seed(SharedRegistrationPath(), document);
        Seed(Sidecar(), SidecarContent);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsTrue();
        await Assert.That(_fileSystem.File.Exists(Sidecar())).IsFalse();
        _ = _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
    }

    [Test]
    public async Task StopAsync_Should_Shut_Persistent_Terminals_Down_Before_Signalling_When_The_Daemon_Is_Ready()
    {
        SeedModern();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        await Stopper().StopAsync(options: null, CancellationToken.None);

        Received.InOrder(() =>
        {
            _ = _ptyShutdown.ShutdownAsync(
                Arg.Is<ServiceRegistration>(registration => registration != null && registration.ProcessId == RegisteredPid),
                Arg.Any<CancellationToken>());
            _ = _processControl.TrySignal(Live, ProcessSignal.Terminate);
        });
    }

    [Test]
    [Arguments((int)ServiceState.Waiting)]
    [Arguments((int)ServiceState.Failed)]
    public async Task StopAsync_Should_Skip_The_Terminal_Shutdown_When_The_Daemon_Is_Not_Ready(int state)
    {
        SeedModern();
        Answer(new ServiceProbeResult((ServiceState)state, Version, TimedOut: false, Compatible: true));

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _ptyShutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
    }

    [Test]
    public async Task StopAsync_Should_Skip_The_Terminal_Shutdown_When_The_Daemon_Is_Incompatible()
    {
        SeedModern();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: false));

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _ptyShutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
    }

    [Test]
    public async Task StopAsync_Should_Skip_The_Terminal_Shutdown_When_The_Probe_Timed_Out()
    {
        SeedModern();
        Answer(new ServiceProbeResult(State: null, Version: null, TimedOut: true, Compatible: true));

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _ptyShutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
    }

    [Test]
    public async Task StopAsync_Should_Ignore_A_Refused_Terminal_Shutdown_And_Still_Terminate()
    {
        SeedModern();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));
        _ptyShutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OpenCodeApiException("The daemon refused the shutdown."));

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
    }

    [Test]
    public async Task StopAsync_Should_Ignore_An_Unreachable_Terminal_Shutdown_And_Still_Terminate()
    {
        SeedModern();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));
        _ptyShutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OpenCodeTransportException("The daemon went away."));

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
    }

    [Test]
    public async Task StopAsync_Should_Propagate_Cancellation_From_The_Terminal_Shutdown_Without_Signalling()
    {
        SeedModern();
        Seed(Sidecar(), SidecarContent);
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));
        using var cancellation = new CancellationTokenSource();
        _ptyShutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cancellation.CancelOnWorkerAsync();
                throw new OperationCanceledException(cancellation.Token);
            });

        _ = await Assert
            .That(async () => await Stopper().StopAsync(options: null, cancellation.Token))
            .Throws<OperationCanceledException>();

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        await Assert.That(_fileSystem.File.Exists(Sidecar())).IsTrue();
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsTrue();
    }

    [Test]
    public async Task StopAsync_Should_Not_Signal_When_The_Registration_Changed_Before_The_First_Signal()
    {
        SeedModern();
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Another service registered under the same file while this stop was probing.
                Seed(SharedRegistrationPath(), ServiceRegistrationData.Minimal);
                return NoService;
            });

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        await Assert.That(ReadText(SharedRegistrationPath())).IsEqualTo(ServiceRegistrationData.Minimal);
    }

    [Test]
    public async Task StopAsync_Should_Remove_The_Registration_Once_The_Terminate_Rung_Ends_The_Process()
    {
        SeedModern();
        Seed(Sidecar(), SidecarContent);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        _ = _processControl.DidNotReceive().TrySignal(Arg.Any<ProcessIdentity>(), ProcessSignal.Kill);
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
        await Assert.That(_fileSystem.File.Exists(Sidecar())).IsFalse();
    }

    [Test]
    public async Task StopAsync_Should_Escalate_To_Kill_When_The_Process_Survives_The_Terminate_Rung()
    {
        SeedModern();
        ProcessLeavesOn(ProcessSignal.Kill);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        Received.InOrder(() =>
        {
            _ = _processControl.TrySignal(Live, ProcessSignal.Terminate);
            _ = _processControl.TrySignal(Live, ProcessSignal.Kill);
        });
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
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
    public async Task StopAsync_Should_Not_Kill_When_The_Registration_Changed_After_The_Terminate_Signal()
    {
        SeedModern();
        ProcessNeverLeaves();
        _processControl.TrySignal(Live, ProcessSignal.Terminate).Returns(_ =>
        {
            Seed(SharedRegistrationPath(), ServiceRegistrationData.Minimal);
            return true;
        });

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        _ = _processControl.DidNotReceive().TrySignal(Arg.Any<ProcessIdentity>(), ProcessSignal.Kill);
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
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsTrue();
    }

    [Test]
    public async Task StopAsync_Should_Remove_The_Registration_Without_Signalling_When_The_Process_Is_Gone()
    {
        SeedModern();
        ProcessIs(null);

        await Stopper().StopAsync(options: null, CancellationToken.None);

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
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
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
    }

    [Test]
    public async Task StopAsync_Should_Report_A_Sidecar_That_Cannot_Be_Removed_Before_Signalling()
    {
        var fileSystem = Substitute.For<IServiceFileSystem>();
        fileSystem.TryReadAllBytesAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(ServiceRegistrationData.Passwordless));
        fileSystem.TryDelete(Sidecar()).Throws(new UnauthorizedAccessException("The sidecar is locked."));
        var stopper = new ServiceStopper(
            _environment,
            fileSystem,
            _probe,
            _ptyShutdown,
            new ServicePtyHandoff(fileSystem, _clock, _ptyShutdown),
            _processControl,
            Timing);

        var exception = await Assert
            .That(async () => await stopper.StopAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.InnerException).IsTypeOf<UnauthorizedAccessException>();
        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
    }

    [Test]
    public async Task StopAsync_Should_Not_Signal_When_Cancelled_Before_The_First_Signal()
    {
        SeedModern();
        using var cancellation = new CancellationTokenSource();
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await cancellation.CancelOnWorkerAsync();
                return NoService;
            });

        _ = await Assert
            .That(async () => await Stopper().StopAsync(options: null, cancellation.Token))
            .Throws<OperationCanceledException>();

        _ = _processControl.DidNotReceiveWithAnyArgs().TrySignal(default, default);
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsTrue();
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

        _ = await Assert
            .That(async () => await Stopper().StopAsync(options: null, cancellation.Token))
            .Throws<OperationCanceledException>();

        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        _ = _processControl.DidNotReceive().TrySignal(Arg.Any<ProcessIdentity>(), ProcessSignal.Kill);
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsTrue();
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
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsFalse();
        await Assert.That(_fileSystem.File.Exists(legacy)).IsTrue();
    }

    [Test]
    public async Task StopAsync_Should_Stop_A_Direct_Passwordless_Registration_Without_The_Environment()
    {
        // Info.password is optional at the pin: a record without one is still a terminate target,
        // and it is still probed, because the CLI's shutdown step discovers before it decides.
        var direct = Path("elsewhere", "registration.json");
        Seed(direct, ServiceRegistrationData.Passwordless);

        await Stopper().StopAsync(new OpenCodeServerStopOptions { RegistrationFilePath = direct }, CancellationToken.None);

        _ = _probe.Received(1).ProbeAsync(
            Arg.Is<ServiceRegistration>(registration => registration != null && registration.Password == null),
            Arg.Any<CancellationToken>());
        _ = _processControl.Received(1).TrySignal(Live, ProcessSignal.Terminate);
        await Assert.That(_fileSystem.File.Exists(direct)).IsFalse();
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

    private ServiceStopper Stopper() =>
        new(
            _environment,
            new TestablyServiceFileSystem(_fileSystem),
            _probe,
            _ptyShutdown,
            new ServicePtyHandoff(new TestablyServiceFileSystem(_fileSystem), _clock, _ptyShutdown),
            _processControl,
            Timing);

    private void Answer(ServiceProbeResult result) =>
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(result);

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
