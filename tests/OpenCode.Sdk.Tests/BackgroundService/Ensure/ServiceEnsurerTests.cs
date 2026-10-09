using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.BackgroundService.Ensure;

/// <summary>
/// The Ensure door over substituted seams: no live process, no real registration, no socket. Each
/// test states one upstream behavior (<c>ensure</c> in <c>promise/service.ts</c>, its contender rules
/// in <c>contenderPool</c> in <c>service-contender.ts</c>, its decision in <c>decide</c> in
/// <c>service-probe.ts</c>, and <c>server-connection.ts:74-84</c>) and asserts what the seams observed
/// — spawns, signals, handoff calls, <c>OnStart</c>, and the instants the test's own clock showed at
/// each — never how many times the loop read the clock.
/// </summary>
public sealed class ServiceEnsurerTests
{
    private const string Version = "2.0.3";
    private const int RegisteredPid = 48213;
    private const int ElectedPid = 51000;
    private static readonly DateTimeOffset Start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri ElectedEndpoint = new("http://127.0.0.1:49375");
    private static readonly ServiceTiming Timing = new(
        RequestTimeout: TimeSpan.FromSeconds(2),
        PollInterval: TimeSpan.Zero,
        SpawnDelay: TimeSpan.Zero,
        MaxSpawnDelay: TimeSpan.FromSeconds(30),
        PromiseTimeout: TimeSpan.FromSeconds(120),
        StopPollInterval: TimeSpan.FromMilliseconds(1),
        StopPollAttempts: 3);

    private static readonly ServiceProbeResult Ready = new(ServiceState.Ready, Version, TimedOut: false, Compatible: true);
    private static readonly ServiceProbeResult Waiting = new(ServiceState.Waiting, Version, TimedOut: false, Compatible: true);
    private static readonly ServiceProbeResult Failed = new(ServiceState.Failed, Version, TimedOut: false, Compatible: true);
    private static readonly ServiceProbeResult TimedOut = new(State: null, Version: null, TimedOut: true, Compatible: true);

    /// <summary>The probe was refused: nothing answers at the registered endpoint, as when the registered process is dead.</summary>
    private static readonly ServiceProbeResult Refused = new(State: null, Version: null, TimedOut: false, Compatible: true);

    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();
    private readonly IServiceInfoProbe _probe = Substitute.For<IServiceInfoProbe>();
    private readonly IServiceContenderSpawner _spawner = Substitute.For<IServiceContenderSpawner>();
    private readonly IServicePtyHandoff _handoff = Substitute.For<IServicePtyHandoff>();
    private readonly IServiceProcessControl _processControl = Substitute.For<IServiceProcessControl>();
    private readonly IServiceClock _clock = Substitute.For<IServiceClock>();
    private readonly List<IServiceContender> _spawned = [];
    private readonly List<IServiceContenderSpawner.ContenderStartInfo> _starts = [];
    private readonly HashSet<int> _signalled = [];
    private readonly List<(OpenCodeServerEnsureReason Reason, string? Previous)> _announced = [];
    private readonly List<DateTimeOffset> _refusedAt = [];
    private readonly List<DateTimeOffset> _spawnedAt = [];
    private DateTimeOffset _now = Start;

    public ServiceEnsurerTests()
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(Root("state"));
        _environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(Root("config"));
        // Bounded by default: an implementation that loops where it should refuse times out, never hangs.
        DeadlineAfter(clockReads: 5_000);
        _handoff.EnvironmentAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => PassThrough(call.Arg<IReadOnlyDictionary<string, string>?>()));
        _handoff.CompleteAsync(Arg.Any<string>(), Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _handoff.ClearAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _handoff.PrepareAsync(Arg.Any<string>(), Arg.Any<ServiceRegistration>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // A process is live until it is signalled; the terminator's poll then sees it gone.
        _processControl.TrySnapshot(Arg.Any<int>())
            .Returns(call => _signalled.Contains(call.Arg<int>()) ? null : new ProcessIdentity(call.Arg<int>(), Start.UtcDateTime));
        _processControl.TrySignal(Arg.Any<ProcessIdentity>(), Arg.Any<ProcessSignal>())
            .Returns(call =>
            {
                _ = _signalled.Add(call.Arg<ProcessIdentity>().ProcessId);
                return true;
            });

        // By default a spawn stays live and never registers.
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(call =>
        {
            _starts.Add(call.Arg<IServiceContenderSpawner.ContenderStartInfo>()!);
            return LiveContender();
        });
    }

    [Test]
    public async Task EnsureAsync_Should_Return_A_Ready_Compatible_Service_Without_Spawning()
    {
        SeedRegistered();
        AnswerRegistered(Ready);

        var registration = await Ensurer().EnsureAsync(Announcing(), CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(RegisteredPid);
        await Assert.That(registration.Password).IsEqualTo(ServiceRegistrationData.Password);
        await Assert.That(_starts).IsEmpty();
        await Assert.That(_announced).IsEmpty();
        _ = _handoff.Received(1).CompleteAsync(SharedRegistrationPath(), registration, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureAsync_Should_Wait_For_A_Starting_Service_Until_It_Is_Ready()
    {
        SeedRegistered();
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(Waiting, Waiting, Ready);

        var registration = await Ensurer().EnsureAsync(Announcing(), CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(RegisteredPid);
        await Assert.That(_starts).IsEmpty();
        await Assert.That(_announced).IsEmpty();
    }

    [Test]
    public async Task EnsureAsync_Should_Throw_When_A_Compatible_Service_Failed()
    {
        SeedRegistered();
        AnswerRegistered(Failed);

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(ServiceEnsurer.FailedMessage);
        await Assert.That(_starts).IsEmpty();
    }

    [Test]
    public async Task EnsureAsync_Should_Ignore_ExpectedVersion_When_The_Policy_Is_Ignore()
    {
        SeedRegistered();
        AnswerRegistered(Ready);

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.2", VersionPolicy = OpenCodeServerVersionPolicy.Ignore },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(RegisteredPid);
        await Assert.That(_starts).IsEmpty();
    }

    [Test]
    public async Task EnsureAsync_Should_Throw_On_A_Running_Mismatch_Without_Entering_The_Loop_When_The_Policy_Is_Error()
    {
        SeedRegistered();
        AnswerRegistered(Ready);

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(
                new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.2", VersionPolicy = OpenCodeServerVersionPolicy.Error },
                CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(ServiceEnsurer.MismatchMessage);
        await Assert.That(_starts).IsEmpty();
        await Assert.That(_signalled).IsEmpty();
    }

    [Test]
    public async Task EnsureAsync_Should_Return_A_Matching_Service_Without_Entering_The_Loop_When_The_Policy_Is_Error()
    {
        SeedRegistered();
        AnswerRegistered(Ready);

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = Version, VersionPolicy = OpenCodeServerVersionPolicy.Error },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(RegisteredPid);
        await Assert.That(_starts).IsEmpty();
    }

    [Test]
    public async Task EnsureAsync_Should_Enter_The_Election_When_Nothing_Is_Registered_Under_The_Error_Policy()
    {
        ElectOnSpawn("2.0.4");

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Error },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_starts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EnsureAsync_Should_Replace_A_Ready_Mismatched_Service_By_Handing_Off_Then_Terminating_Then_Spawning()
    {
        SeedRegistered();
        AnswerRegistered(Ready);
        ElectOnSpawn("2.0.4");

        var registration = await Ensurer().EnsureAsync(
            Announcing(new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace }),
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.VersionMismatch, (string?)Version)]);
        _ = _handoff.Received(1).PrepareAsync(SharedRegistrationPath(), WithPid(RegisteredPid), Timing.RequestTimeout, Arg.Any<CancellationToken>());
        _ = _handoff.DidNotReceiveWithAnyArgs().ClearAsync(default!, default);
        await Assert.That(_signalled).Contains(RegisteredPid);
        await Assert.That(_starts.Count).IsEqualTo(1);
    }

    /// <summary>
    /// Another service registered while the mismatched one was being probed. The replacement stop
    /// reads the registration afresh, as upstream's <c>stop</c> does: the handoff is prepared for the
    /// service registered now, and that service is the one terminated.
    /// </summary>
    [Test]
    public async Task EnsureAsync_Should_Replace_The_Service_Registered_When_The_Replacement_Stops()
    {
        const int successorPid = 48214;
        SeedRegistered();
        _probe.ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Seed(SharedRegistrationPath(), ServiceRegistrationData.DuplicatePidResolved);
            return Ready;
        });
        _probe.ProbeAsync(WithPid(successorPid), Arg.Any<CancellationToken>()).Returns(Ready);
        ElectOnSpawn("2.0.4");

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        _ = _handoff.Received(1).PrepareAsync(SharedRegistrationPath(), WithPid(successorPid), Timing.RequestTimeout, Arg.Any<CancellationToken>());
        _ = _handoff.DidNotReceive().PrepareAsync(SharedRegistrationPath(), WithPid(RegisteredPid), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await Assert.That(_signalled).Contains(successorPid);
        await Assert.That(_signalled).DoesNotContain(RegisteredPid);
    }

    [Test]
    public async Task EnsureAsync_Should_Clear_Rather_Than_Hand_Off_When_The_Mismatched_Service_Is_Not_Ready()
    {
        SeedRegistered();
        AnswerRegistered(Waiting);
        ElectOnSpawn("2.0.4");

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        _ = _handoff.Received(1).ClearAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>());
        _ = _handoff.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
        await Assert.That(_signalled).Contains(RegisteredPid);
    }

    [Test]
    public async Task EnsureAsync_Should_Clear_Rather_Than_Hand_Off_When_The_Mismatched_Service_Failed()
    {
        // service-decision.test.ts: a version mismatch is replaced whatever the state, and only a
        // ready service has terminals to hand off; a failed one is cleared, never reported as failed.
        SeedRegistered();
        AnswerRegistered(Failed);
        ElectOnSpawn("2.0.4");

        var registration = await Ensurer().EnsureAsync(
            Announcing(new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace }),
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.VersionMismatch, (string?)Version)]);
        _ = _handoff.Received(1).ClearAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>());
        _ = _handoff.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
        await Assert.That(_signalled).Contains(RegisteredPid);
    }

    /// <summary>
    /// service.test.ts "replaces a crashed service whose registration names a dead process" and
    /// service-contender-pool.test.ts "an unanswered registration gets one spawn delay before an
    /// attempt competes" are one case at this seam: a registration whose probe is refused. The first
    /// contender starts one spawn delay after the registration first went unanswered, the dead
    /// process is never signalled, and <c>OnStart</c> fires once.
    /// </summary>
    [Test]
    public async Task EnsureAsync_Should_Replace_A_Registration_Nothing_Answers_After_One_Spawn_Delay_And_Announce_Once()
    {
        var spawnDelay = TimeSpan.FromMinutes(1);
        SeedRegistered();
        RefuseRegistered(step: TimeSpan.FromSeconds(15));
        ExitCleanlyUntilElected(electedSpawn: 1);

        var registration = await Ensurer(Timing with { SpawnDelay = spawnDelay }).EnsureAsync(Announcing(), CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_spawnedAt.Single() - _refusedAt[0]).IsEqualTo(spawnDelay);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.Missing, (string?)null)]);
        await Assert.That(_signalled).IsEmpty();
    }

    /// <summary>
    /// service-contender-pool.test.ts "clean exits back off up to the maximum spawn delay": each
    /// contender that exits 0 doubles the delay before the next one, and the doubling stops at the
    /// maximum.
    /// </summary>
    [Test]
    public async Task EnsureAsync_Should_Double_The_Spawn_Delay_After_Each_Clean_Contender_Exit_Up_To_The_Maximum()
    {
        var timing = Timing with { SpawnDelay = TimeSpan.FromSeconds(10), MaxSpawnDelay = TimeSpan.FromSeconds(30) };
        SeedRegistered();
        RefuseRegistered(step: TimeSpan.FromSeconds(1));
        ExitCleanlyUntilElected(electedSpawn: 4);

        var registration = await Ensurer(timing).EnsureAsync(options: null, CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(SpawnGaps()).IsEquivalentTo(
            [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)],
            CollectionOrdering.Matching);
    }

    /// <summary>
    /// service-contender-pool.test.ts "an answering service resets the backoff to the base spawn
    /// delay": after a clean exit doubled the delay, one answer from the registered service brings the
    /// next contender back to the base delay.
    /// </summary>
    [Test]
    public async Task EnsureAsync_Should_Reset_The_Spawn_Delay_When_A_Registered_Service_Answers()
    {
        var timing = Timing with { SpawnDelay = TimeSpan.FromSeconds(10), MaxSpawnDelay = TimeSpan.FromSeconds(60) };
        var reaped = false;
        var answered = false;
        SeedRegistered();
        _clock.UtcNow.Returns(_ => _now);
        _probe.ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _now += TimeSpan.FromSeconds(1);
            if (reaped && !answered)
            {
                // The first round after the clean exit was reaped, and the delay doubled with it.
                answered = true;
                return Waiting;
            }

            return Refused;
        });
        ExitCleanlyUntilElected(electedSpawn: 2, cleanExit => cleanExit.When(static contender => contender.Dispose()).Do(_ => reaped = true));

        var registration = await Ensurer(timing).EnsureAsync(options: null, CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(answered).IsTrue();
        await Assert.That(_spawnedAt[1] - _spawnedAt[0]).IsEqualTo(timing.SpawnDelay);
    }

    /// <summary>
    /// service.test.ts "recovers when the starting service it waits for is stopped": the service
    /// answers waiting, then its registration is gone; nothing is registered any more, so a contender
    /// starts at once rather than after the spawn delay, and Ensure signals nothing itself.
    /// </summary>
    [Test]
    public async Task EnsureAsync_Should_Spawn_At_Once_When_The_Starting_Service_It_Waits_For_Is_Stopped()
    {
        SeedRegistered();
        var probes = 0;
        _probe.ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++probes == 2)
            {
                // The service is stopped between rounds; its registration goes with it.
                _fileSystem.File.Delete(SharedRegistrationPath());
            }

            return Waiting;
        });
        ElectOnSpawn(Version);

        // A spawn delay the fixed clock never reaches: only an immediate recruit can start a contender.
        var registration = await Ensurer(Timing with { SpawnDelay = TimeSpan.FromMinutes(1) })
            .EnsureAsync(Announcing(), CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_starts.Count).IsEqualTo(1);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.Missing, (string?)null)]);
        await Assert.That(_signalled).IsEmpty();
        _ = _probe.Received(2).ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureAsync_Should_Terminate_An_Unresponsive_Service_After_Three_Timeouts_And_Start_Another()
    {
        SeedRegistered();
        _probe.ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>())
            .Returns(TimedOut);
        ElectOnSpawn(Version);

        // A spawn delay the fixed clock never reaches: only the recovery's own lastSpawn reset lets a
        // contender start, so the spawn proves the recovery ran (upstream's default delay is 5 s).
        var registration = await Ensurer(Timing with { SpawnDelay = TimeSpan.FromMinutes(1) })
            .EnsureAsync(Announcing(), CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.Missing, (string?)null)]);
        _ = _probe.Received(3).ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>());
        _ = _handoff.Received(1).ClearAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>());
        await Assert.That(_signalled).Contains(RegisteredPid);
    }

    [Test]
    public async Task EnsureAsync_Should_Layer_The_Caller_Environment_Over_The_Service_Config_Environment()
    {
        Seed(ConfigPath(), new FixtureLoader().LoadJson("BackgroundService.service-config-env.json"));
        ElectOnSpawn(Version);

        _ = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["HTTPS_PROXY"] = "http://caller:8080",
                    ["CALLER_ONLY"] = "1",
                },
            },
            CancellationToken.None);

        var environment = _starts.Single().Environment;
        await Assert.That(environment["HTTPS_PROXY"]).IsEqualTo("http://caller:8080");
        await Assert.That(environment["OPENCODE_DISABLE_MODELS_FETCH"]).IsEqualTo("1");
        await Assert.That(environment["CALLER_ONLY"]).IsEqualTo("1");
    }

    [Test]
    public async Task EnsureAsync_Should_Refuse_An_Empty_Command_Before_Touching_Anything()
    {
        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(new OpenCodeServerEnsureOptions { Command = [] }, CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task EnsureAsync_Should_Refuse_A_Blank_Environment_Key_Before_Touching_Anything()
    {
        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(
                new OpenCodeServerEnsureOptions { Environment = new Dictionary<string, string>(StringComparer.Ordinal) { [" "] = "x" } },
                CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    [Arguments(OpenCodeServerVersionPolicy.Replace)]
    [Arguments(OpenCodeServerVersionPolicy.Error)]
    public async Task EnsureAsync_Should_Refuse_A_Version_Policy_Without_An_Expected_Version(OpenCodeServerVersionPolicy policy)
    {
        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(new OpenCodeServerEnsureOptions { VersionPolicy = policy }, CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task EnsureAsync_Should_Refuse_An_Undefined_Version_Policy()
    {
        _ = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(
                new OpenCodeServerEnsureOptions { ExpectedVersion = Version, VersionPolicy = (OpenCodeServerVersionPolicy)7 },
                CancellationToken.None))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task EnsureAsync_Should_Never_Return_A_Registration_Without_A_Password()
    {
        // Discover refuses such a registration: the handle's credential is non-null by contract.
        Seed(SharedRegistrationPath(), ServiceRegistrationData.Passwordless);
        AnswerRegistered(Ready);
        ElectOnSpawn(Version);

        var registration = await Ensurer().EnsureAsync(options: null, CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(registration.Password).IsNotNull();
    }

    [Test]
    public async Task EnsureAsync_Should_Throw_The_Contender_Failure_When_None_Remain_Live()
    {
        var failure = new OpenCodeServerException("The background service contender (pid 9000) exited with code 1.");
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(_ => FinishedContender(failure));

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception).IsSameReferenceAs(failure);
        _spawned.Single().Received(1).Dispose();
    }

    [Test]
    public async Task EnsureAsync_Should_Wrap_A_Spawn_Failure()
    {
        var cause = new InvalidOperationException("spawn refused");
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(_ => throw cause);

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    public async Task EnsureAsync_Should_Propagate_Cancellation_That_Arrives_During_A_Spawn()
    {
        using var cancellation = new CancellationTokenSource();
        _handoff.EnvironmentAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, string?>>>(async call =>
            {
                await cancellation.CancelOnWorkerAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return PassThrough(null);
            });

        _ = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task EnsureAsync_Should_Release_Live_Contenders_When_The_Caller_Cancels()
    {
        // The first spawn goes through and its contender stays live; the caller cancels while the
        // loop prepares the second one.
        using var cancellation = new CancellationTokenSource();
        var preparations = 0;
        _handoff.EnvironmentAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, string?>>>(async call =>
            {
                if (++preparations == 2)
                {
                    await cancellation.CancelOnWorkerAsync();
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                }

                return PassThrough(null);
            });

        _ = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, cancellation.Token))
            .Throws<OperationCanceledException>();

        _spawned.Single().Received(1).Release();
    }

    [Test]
    public async Task EnsureAsync_Should_Keep_At_Most_Two_Live_Contenders_And_Announce_Missing_Once()
    {
        DeadlineAfter(clockReads: 200);

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(Announcing(), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(ServiceEnsurer.TimeoutMessage);
        await Assert.That(_starts.Count).IsEqualTo(2);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.Missing, (string?)null)]);
        foreach (var contender in _spawned)
        {
            contender.Received(1).Release();
        }
    }

    [Test]
    public async Task EnsureAsync_Should_Carry_The_Last_Swallowed_Replacement_Failure_Into_The_Timeout()
    {
        // The mismatched daemon never leaves: every replacement stop fails after the kill rung,
        // upstream swallows it and polls again, and the timeout should say why.
        SeedRegistered();
        AnswerRegistered(Ready);
        _processControl.TrySnapshot(Arg.Any<int>()).Returns(call => new ProcessIdentity(call.Arg<int>(), Start.UtcDateTime));
        DeadlineAfter(clockReads: 40);

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(
                new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace },
                CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(ServiceEnsurer.TimeoutMessage);
        await Assert.That(exception.InnerException).IsTypeOf<OpenCodeServerException>();
        await Assert.That(exception.InnerException!.Message).Contains("still running after the kill rung");
    }

    [Test]
    public async Task EnsureAsync_Should_Throw_On_An_Incompatible_Health_Protocol_Whose_Version_Matches()
    {
        // service-reconnect.test.ts: a registered owner whose health endpoint disappeared is neither
        // signalled, handed off, nor replaced while no unmet version requirement says otherwise.
        SeedRegistered();
        AnswerRegistered(Ready with { Compatible = false });

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(Announcing(), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(ServiceEnsurer.IncompatibleProtocolMessage);
        await Assert.That(_starts).IsEmpty();
        await Assert.That(_signalled).IsEmpty();
        await Assert.That(_announced).IsEmpty();
        _ = _handoff.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
        _ = _handoff.DidNotReceiveWithAnyArgs().ClearAsync(default!, default);
    }

    [Test]
    public async Task EnsureAsync_Should_Replace_An_Incompatible_Health_Protocol_When_The_Version_Requirement_Is_Unmet()
    {
        SeedRegistered();
        AnswerRegistered(Ready with { Compatible = false });
        ElectOnSpawn("2.0.4");

        var registration = await Ensurer().EnsureAsync(
            Announcing(new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace }),
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_announced).IsEquivalentTo([(OpenCodeServerEnsureReason.VersionMismatch, (string?)Version)]);
        await Assert.That(_signalled).Contains(RegisteredPid);
    }

    [Test]
    public async Task EnsureAsync_Should_Terminate_A_Mismatched_Service_Even_When_The_Handoff_Fails()
    {
        // The client's stop only warns when the handoff fails; the old service is ended either way.
        SeedRegistered();
        AnswerRegistered(Ready);
        ElectOnSpawn("2.0.4");
        _handoff.PrepareAsync(Arg.Any<string>(), Arg.Any<ServiceRegistration>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OpenCodeServerException("The sidecar could not be published.")));

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_signalled).Contains(RegisteredPid);
    }

    [Test]
    public async Task EnsureAsync_Should_Terminate_A_Mismatched_Service_That_Is_Not_Ready_Even_When_The_Clear_Fails()
    {
        // A service that is not ready has no terminals to hand off, so the stop clears the sidecar
        // instead; the client only warns when that fails, and the old service is ended either way.
        SeedRegistered();
        AnswerRegistered(Waiting);
        ElectOnSpawn("2.0.4");
        _handoff.ClearAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new OpenCodeServerException("The sidecar could not be removed.")));

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        _ = _handoff.Received(1).ClearAsync(SharedRegistrationPath(), Arg.Any<CancellationToken>());
        _ = _handoff.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default, default);
        await Assert.That(_signalled).Contains(RegisteredPid);
    }

    [Test]
    public async Task EnsureAsync_Should_Release_The_Replaced_Contender_And_Drop_A_Held_Failure_After_A_Version_Mismatch()
    {
        // The first contender stays live, the second fails and its failure is held; then the first
        // registers at the old version. The replacement ends it, releases it, drops the held
        // failure, and recruits the winner.
        const int replacedPid = 9000;
        var failure = new OpenCodeServerException("The background service contender (pid 9001) exited with code 23.");
        _probe.ProbeAsync(WithPid(replacedPid), Arg.Any<CancellationToken>()).Returns(Ready);
        _probe.ProbeAsync(WithPid(ElectedPid), Arg.Any<CancellationToken>())
            .Returns(new ServiceProbeResult(ServiceState.Ready, "2.0.4", TimedOut: false, Compatible: true));
        var spawns = 0;
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(call =>
        {
            _starts.Add(call.Arg<IServiceContenderSpawner.ContenderStartInfo>()!);
            switch (++spawns)
            {
                case 1:
                    return LiveContender();
                case 2:
                    var failing = LiveContender();
                    failing.Finished.Returns(true);
                    failing.TryGetFailure().Returns(_ =>
                    {
                        // The harvest that holds this failure is when the first contender registers.
                        Seed(SharedRegistrationPath(), ServiceRegistrationDocument.Compose("srv_old", Version, ElectedEndpoint, replacedPid, "old-p455"));
                        return failure;
                    });
                    return failing;
                default:
                    Seed(SharedRegistrationPath(), ServiceRegistrationDocument.Compose("srv_elected", "2.0.4", ElectedEndpoint, ElectedPid, "elected-p455"));
                    return LiveContender();
            }
        });

        var registration = await Ensurer().EnsureAsync(
            new OpenCodeServerEnsureOptions { ExpectedVersion = "2.0.4", VersionPolicy = OpenCodeServerVersionPolicy.Replace },
            CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_starts.Count).IsEqualTo(3);
        await Assert.That(_signalled).Contains(replacedPid);
        _spawned[0].Received(1).Release();
        _spawned[0].Received(1).Dispose();
    }

    [Test]
    public async Task EnsureAsync_Should_Hold_A_Contender_Failure_Without_Recruiting_Replacements_Until_The_Bound()
    {
        // service.test.ts "retains a contender failure until the deadline while its survivor
        // stalls": the first contender stays live, the second fails; no third is recruited, and the
        // bound expires with the failure rather than with the timeout.
        var failure = new OpenCodeServerException("The background service contender (pid 9001) exited with code 23.");
        var spawns = 0;
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(call =>
        {
            _starts.Add(call.Arg<IServiceContenderSpawner.ContenderStartInfo>()!);
            return ++spawns == 2 ? FinishedContender(failure) : LiveContender();
        });
        DeadlineAfter(clockReads: 200);

        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception).IsSameReferenceAs(failure);
        await Assert.That(_starts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task EnsureAsync_Should_Release_The_Evicted_Contender_And_Drop_A_Held_Failure_After_Recovery()
    {
        // service.test.ts "recovers when an unresponsive contender is evicted after a prior
        // failure": the first contender registers and stops answering, the second fails; the
        // recovery ends the first, releases it, drops the held failure, and recruits a winner.
        const int stalledPid = 9000;
        var failure = new OpenCodeServerException("The background service contender (pid 9001) exited with code 23.");
        _probe.ProbeAsync(WithPid(stalledPid), Arg.Any<CancellationToken>()).Returns(TimedOut);
        _probe.ProbeAsync(WithPid(ElectedPid), Arg.Any<CancellationToken>()).Returns(Ready);
        var spawns = 0;
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(call =>
        {
            _starts.Add(call.Arg<IServiceContenderSpawner.ContenderStartInfo>()!);
            switch (++spawns)
            {
                case 1:
                    Seed(SharedRegistrationPath(), ServiceRegistrationDocument.Compose("srv_stalled", Version, ElectedEndpoint, stalledPid, "stalled-p455"));
                    return LiveContender();
                case 2:
                    return FinishedContender(failure);
                default:
                    Seed(SharedRegistrationPath(), ServiceRegistrationDocument.Compose("srv_elected", Version, ElectedEndpoint, ElectedPid, "elected-p455"));
                    return LiveContender();
            }
        });

        var registration = await Ensurer().EnsureAsync(options: null, CancellationToken.None);

        await Assert.That(registration.ProcessId).IsEqualTo(ElectedPid);
        await Assert.That(_starts.Count).IsEqualTo(3);
        await Assert.That(_signalled).Contains(stalledPid);
        _spawned[0].Received(1).Release();
        _spawned[0].Received(1).Dispose();
    }

    [Test]
    public async Task EnsureAsync_Should_Refuse_Contradictory_Options_Before_Touching_Anything()
    {
        var exception = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(
                new OpenCodeServerEnsureOptions { Channel = "dev", RegistrationFilePath = Path("elsewhere", "registration.json") },
                CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task EnsureAsync_Should_Rethrow_A_Token_Cancelled_Before_The_Call()
    {
        _ = await Assert
            .That(async () => _ = await Ensurer().EnsureAsync(options: null, new CancellationToken(canceled: true)))
            .Throws<OperationCanceledException>();
    }

    private ServiceEnsurer Ensurer(ServiceTiming? timing = null) =>
        new(
            _environment,
            new TestablyServiceFileSystem(_fileSystem),
            _probe,
            _spawner,
            _handoff,
            _processControl,
            _clock,
            new ExecutableResolver(new ExecutableSearchEnvironment
            {
                IsWindows = false,
                SearchPath = "/bin",
                SearchExtensions = null,
                CurrentDirectory = "/",
                FileExists = static _ => true,
            }),
            timing ?? Timing);

    private OpenCodeServerEnsureOptions Announcing(OpenCodeServerEnsureOptions? options = null)
    {
        options ??= new OpenCodeServerEnsureOptions();
        options.OnStart = (reason, previous) => _announced.Add((reason, previous));
        return options;
    }

    /// <summary>Lets the loop run for a while, then moves the clock past the deadline so the loop ends on its own bound.</summary>
    private void DeadlineAfter(int clockReads)
    {
        var reads = 0;
        _clock.UtcNow.Returns(_ => ++reads > clockReads ? Start + Timing.PromiseTimeout : Start);
    }

    private static ServiceRegistration WithPid(int processId) =>
        Arg.Is<ServiceRegistration>(registration => registration != null && registration.ProcessId == processId);

    private void AnswerRegistered(ServiceProbeResult result) =>
        _probe.ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>()).Returns(result);

    /// <summary>
    /// The clock becomes the test's own: it shows <see cref="_now"/>, and every probe of the
    /// registered daemon is one poll round that passes <paramref name="step"/> and is refused. The
    /// instant each refusal was seen is recorded, so a verdict rests on those instants; the bound still
    /// ends a loop that never elects.
    /// </summary>
    private void RefuseRegistered(TimeSpan step)
    {
        _clock.UtcNow.Returns(_ => _now);
        _probe.ProbeAsync(WithPid(RegisteredPid), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _now += step;
            _refusedAt.Add(_now);
            return Refused;
        });
    }

    /// <summary>
    /// Records the instant of every spawn. The spawn numbered <paramref name="electedSpawn"/> registers
    /// a ready daemon the way a winning contender does; every one before it exits 0 without
    /// registering, and <paramref name="onCleanExit"/> sees each of those.
    /// </summary>
    private void ExitCleanlyUntilElected(int electedSpawn, Action<IServiceContender>? onCleanExit = null)
    {
        _probe.ProbeAsync(WithPid(ElectedPid), Arg.Any<CancellationToken>()).Returns(Ready);
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(call =>
        {
            _starts.Add(call.Arg<IServiceContenderSpawner.ContenderStartInfo>()!);
            _spawnedAt.Add(_now);
            var contender = LiveContender();
            if (_starts.Count == electedSpawn)
            {
                Seed(SharedRegistrationPath(), ServiceRegistrationDocument.Compose("srv_elected", Version, ElectedEndpoint, ElectedPid, "elected-p455"));
                return contender;
            }

            contender.Finished.Returns(true);
            contender.ExitedZero.Returns(true);
            onCleanExit?.Invoke(contender);
            return contender;
        });
    }

    /// <summary>The time from the first refused probe to the first spawn, then between consecutive spawns.</summary>
    private List<TimeSpan> SpawnGaps() =>
        [.. _spawnedAt.Select((at, index) => at - (index == 0 ? _refusedAt[0] : _spawnedAt[index - 1]))];

    /// <summary>The next spawn registers a ready daemon at <paramref name="version"/>, the way a winning contender does.</summary>
    private void ElectOnSpawn(string version)
    {
        _probe.ProbeAsync(WithPid(ElectedPid), Arg.Any<CancellationToken>())
            .Returns(new ServiceProbeResult(ServiceState.Ready, version, TimedOut: false, Compatible: true));
        _spawner.Spawn(Arg.Any<IServiceContenderSpawner.ContenderStartInfo>()).Returns(call =>
        {
            _starts.Add(call.Arg<IServiceContenderSpawner.ContenderStartInfo>()!);
            Seed(SharedRegistrationPath(), ServiceRegistrationDocument.Compose("srv_elected", version, ElectedEndpoint, ElectedPid, "elected-p455"));
            return LiveContender();
        });
    }

    private IServiceContender LiveContender()
    {
        var contender = Substitute.For<IServiceContender>();
        contender.ProcessId.Returns(9000 + _spawned.Count);
        _spawned.Add(contender);
        return contender;
    }

    private IServiceContender FinishedContender(OpenCodeServerException failure)
    {
        var contender = LiveContender();
        contender.Finished.Returns(true);
        contender.TryGetFailure().Returns(failure);
        return contender;
    }

    private static Dictionary<string, string?> PassThrough(IReadOnlyDictionary<string, string>? caller)
    {
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (caller is not null)
        {
            foreach (var entry in caller)
            {
                overlay[entry.Key] = entry.Value;
            }
        }

        return overlay;
    }

    private void SeedRegistered() =>
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));

    private void Seed(string path, string content)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
#pragma warning disable MA0045 // MockFileSystem has no async write on every TFM; same Seed as ServiceDiscoveryTests.
        _fileSystem.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
#pragma warning restore MA0045
    }

    private string SharedRegistrationPath() => _fileSystem.Path.Combine(Root("state"), "opencode", "service.json");

    private string ConfigPath() => _fileSystem.Path.Combine(Root("config"), "opencode", "service.json");

    private string Root(string name) => Path(name);

    private string Path(params string[] segments) =>
        _fileSystem.Path.Combine([_fileSystem.Path.GetTempPath(), "service-ensure", .. segments]);
}
