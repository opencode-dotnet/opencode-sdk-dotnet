using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Ensure;

/// <summary>
/// Exact-pin process evidence for the Ensure door: the accepted pin's own
/// <c>serve --service</c> daemon, started by Ensure itself under isolated roots — reusing the
/// default <c>opencode serve --service</c> command resolved through the forwarding shim (test a),
/// elected by ten concurrent callers (test b), recovered from an unresponsive registered daemon
/// after three timeouts (test c), and replacing a version-mismatched one (test d) — and, on the
/// distributed-build consumer leg, the published CLI started on its release channel. Each test owns
/// its own <see cref="EnsureServiceContext"/>; none touches the maintainer's real daemon.
/// </summary>
/// <remarks>
/// Keyless <c>[NotInParallel]</c> rather than the server-process key, the rule for a test whose
/// assertion depends on a wall-clock bound the host can miss under load: the
/// three-timeout recovery and the replace proof both ride the pinned probe bound and a process exit
/// the host can delay, the same profile as the stop liveness proof. Running alone after
/// every other test keeps the host quiet while those bounds hold. The class is a
/// <see cref="TestCategories.ServiceElection"/> test: its elections start more servers than a
/// runner can host beside another host's timed tests, so the gate runs it in a pass of its own.
/// </remarks>
[NotInParallel]
[Category(TestCategories.ServiceElection)]
[ClassDataSource<PinnedManagedServiceFixture>(Shared = SharedType.PerTestSession)]
public sealed class OpenCodeServerEnsureLiveTests(PinnedManagedServiceFixture service)
{
    private static readonly RealFileSystem FileSystem = new();
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The election timing test (c) injects: a fast poll, the pinned request bound kept — it applies
    /// to the real source-run contender too, and a shorter one let three slow first answers terminate
    /// the SDK's own fresh contender under a loaded runner — and a spawn delay past three of those
    /// bounds, so the recovery ends the stalled daemon before the delay could start a contender that
    /// takes the registration over (the pinned loop spawns once the delay passes, recovery or not).
    /// </summary>
    private static readonly ServiceTiming Accelerated = ServiceTiming.Default with
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        SpawnDelay = TimeSpan.FromSeconds(10),
    };

    private EnsureServiceContext? _context;

    private EnsureServiceContext Context => _context ?? throw new InvalidOperationException("The Ensure context is created before each test.");

    [Before(Test)]
    public async Task CreateContextAsync(CancellationToken cancellationToken) =>
        _context = await EnsureServiceContext.CreateAsync(cancellationToken);

    [After(Test)]
    public async Task DisposeContextAsync()
    {
        if (_context is not { } context)
        {
            return;
        }

        if (TestContext.Current?.Execution.Result?.State == TestState.Failed)
        {
            context.KeepForDiagnosis();
        }

        await context.DisposeAsync();
    }

    [Test]
    [Timeout(60_000)]
    public async Task EnsureAsync_Should_Reuse_A_Running_Service_Without_Starting_Another(CancellationToken cancellationToken)
    {
        // A command no PATH resolves: any spawn attempt would fail the call, so success proves reuse.
        var announced = new List<OpenCodeServerEnsureReason>();
        await using var server = await OpenCodeServer.EnsureAsync(
            new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = service.RegistrationFile,
                Command = ["no-such-opencode-" + Guid.NewGuid().ToString("N")],
                OnStart = (reason, _) => announced.Add(reason),
            },
            cancellationToken);

        await Assert.That(server.ProcessId).IsEqualTo(service.ProcessId);
        await Assert.That(server.Endpoint).IsEqualTo(service.Endpoint);
        await Assert.That(announced).IsEmpty();
    }

    /// <summary>
    /// The distributed-build consumer leg (<c>OPENCODE_SDK_TESTS_SERVER_COMMAND</c>) proves Ensure
    /// against the published CLI, not only the source run: the build registers under the release
    /// channel's <c>service.json</c>, reports a real release version rather than the source run's
    /// <c>local</c>, and answers as the process Ensure elected; Stop then ends that process and
    /// removes its registration. Without the variable the proof belongs to that leg, and this run
    /// records the branch it took.
    /// </summary>
    [Test]
    [Timeout(180_000)]
    public async Task EnsureAsync_Then_StopAsync_Should_Start_And_End_The_Distributed_Build_On_Its_Release_Channel(CancellationToken cancellationToken)
    {
        if (PinnedServerCommandOverride.FromEnvironment() is not { } distributed)
        {
            Console.WriteLine("branch: source run — OPENCODE_SDK_TESTS_SERVER_COMMAND="
                + (Environment.GetEnvironmentVariable("OPENCODE_SDK_TESTS_SERVER_COMMAND") ?? "(unset)")
                + "; the consumer leg runs this proof");
            return;
        }

        await using var release = await EnsureServiceContext.CreateForReleaseBuildAsync(cancellationToken);
        await using var server = await release.EnsureAsync(
            new OpenCodeServerEnsureOptions
            {
                RegistrationFilePath = release.RegistrationFile,
                Command = [distributed.Command[0], "serve", "--service"],
                Environment = release.Environment,
            },
            cancellationToken);

        var registration = await ReadRegistrationAsync(release.RegistrationFile);
        using var client = server.CreateClient();
        var info = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

        await Assert.That(registration.ProcessId).IsEqualTo(server.ProcessId);
        await Assert.That(registration.Version).IsNotNull().And.IsNotEqualTo("local");
        await Assert.That(info.ServerInfo.Pid).IsEqualTo(server.ProcessId);
        await Assert.That(info.ServerInfo.Version).IsEqualTo(registration.Version);

        // The published build is the one that can run from an installed image, which on Windows it
        // keeps a second hard link to while it serves; Stop ends it by pid and start time all the same.
        await OpenCodeServer.StopAsync(
            new OpenCodeServerStopOptions { RegistrationFilePath = release.RegistrationFile },
            cancellationToken);

        await Assert.That(release.RegistrationFile).Satisfies(path => !FileSystem.File.Exists(path));
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(server.ProcessId, ExitBound, cancellationToken)).IsTrue()
            .Because("the published build's daemon should leave once Stop ends it");
    }

    [Test]
    [Timeout(180_000)]
    public async Task EnsureAsync_Should_Start_The_Source_Run_Daemon_By_Default_Command_And_Channel(CancellationToken cancellationToken)
    {
        var context = Context;

        // The default command resolves through the isolated process's PATH, whose first entry is
        // the forwarding shim: the shipped PATH/PATHEXT resolution, not an explicit path.
        var result = await new ServiceFixtureCommand(FileSystem)
            .RunAsync(["ensure-channel", "local", context.LedgerPath], context.IsolatedProcessEnvironment(), cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        context.Isolation.ConfirmHonored("The service the fixture process elected");

        var registration = await ReadRegistrationAsync(context.RegistrationFile);
        await Assert.That(result.StandardOutput.Trim())
            .IsEqualTo(ServiceFixtureOutput.EnsuredLine(registration.ProcessId, registration.Endpoint))
            .Because(result.StandardError);

        // A source run compiles its identity as the literal `local` (packages/cli/src/version.ts).
        await Assert.That(registration.Version).IsEqualTo("local");
        await Assert.That(ProcessObservation.IsRunning(registration.ProcessId)).IsTrue();

        // The fixture process recorded the contenders; this process has to recognize the elected
        // service's host among them to end it — on Linux the two read one start time differently.
        await Assert.That(await context.ReadLiveContendersAsync(cancellationToken)).IsNotEmpty();
    }

    [Test]
    [Timeout(180_000)]
    public async Task EnsureAsync_Should_Elect_One_Service_Across_Ten_Concurrent_Callers(CancellationToken cancellationToken)
    {
        var context = Context;

        var options = EnsureOptions(context);
        var servers = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => context.EnsureAsync(options, cancellationToken)));

        try
        {
            var elected = servers[0];
            await Assert.That(ProcessObservation.IsRunning(elected.ProcessId)).IsTrue();

            // Every caller's handle names the same registration: one service was elected.
            foreach (var server in servers)
            {
                await Assert.That(server.ProcessId).IsEqualTo(elected.ProcessId);
                await Assert.That(server.Endpoint).IsEqualTo(elected.Endpoint);
            }

            // Every loser leaves on its own once it finds the elected service, which the pinned
            // CLI's own election test asserts before it ends the winner.
            await Assert.That(await context.SettleAsync(cancellationToken)).IsTrue()
                .Because("every losing contender should exit once one service is elected");
            await Assert.That(ProcessObservation.IsRunning(elected.ProcessId)).IsTrue();
        }
        finally
        {
            foreach (var server in servers)
            {
                await server.DisposeAsync();
            }
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task EnsureAsync_Should_Recover_From_An_Unresponsive_Daemon_After_Three_Timeouts(CancellationToken cancellationToken)
    {
        var context = Context;
        await using var stall = await ServiceFixtureProcess.StartDaemonStandInAsync(FileSystem, "stall", cancellationToken);

        await SeedAsync(context.RegistrationFile, ServiceRegistrationDocument.Compose(
            "stalled", "local", stall.Endpoint, stall.ProcessId, "stall-p455"));

        var server = await context.EnsureAsync(EnsureOptions(context), Accelerated, cancellationToken);
        try
        {
            await Assert.That(await stall.ObserveExitWithinAsync(ExitBound, cancellationToken)).IsTrue()
                .Because("the unresponsive daemon should be terminated after three timeouts");
            await Assert.That(server.ProcessId).IsNotEqualTo(stall.ProcessId);
            await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsTrue();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task EnsureAsync_Should_Replace_A_Version_Mismatched_Service(CancellationToken cancellationToken)
    {
        var context = Context;
        await using var stale = await ServiceFixtureProcess.StartDaemonStandInAsync(FileSystem, "stale", cancellationToken);

        // A registration naming the stale daemon's own identity: the probe reads it ready at the
        // never-built 0.0.0-stale version, which the Replace policy refuses.
        await SeedAsync(context.RegistrationFile, ServiceRegistrationDocument.Compose(
            "stale", "0.0.0-stale", stale.Endpoint, stale.ProcessId, "stale-p455"));

        var options = EnsureOptions(context);
        options.ExpectedVersion = "local";
        options.VersionPolicy = OpenCodeServerVersionPolicy.Replace;

        var server = await context.EnsureAsync(options, cancellationToken);
        try
        {
            await Assert.That(await stale.ObserveExitWithinAsync(ExitBound, cancellationToken)).IsTrue()
                .Because("the version-mismatched service should be terminated by the replace");
            await Assert.That(server.ProcessId).IsNotEqualTo(stale.ProcessId);
            await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsTrue();

            // The replacement is the source-run daemon at its own version, now the registered one.
            var registration = await ReadRegistrationAsync(context.RegistrationFile);
            await Assert.That(registration.Version).IsEqualTo("local");
            await Assert.That(registration.ProcessId).IsEqualTo(server.ProcessId);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task EnsureAsync_Then_StopAsync_In_One_Host_Should_End_The_Daemon_And_Remove_Its_Registration(CancellationToken cancellationToken)
    {
        // The Unix shim execs bun, so the registered daemon is this host's own direct child: the
        // host that started the shared service is the one stopping it, and nothing but this host
        // can reap it. On Windows the shim's cmd.exe host sits in between; the flow is the same.
        var context = Context;

        var server = await context.EnsureAsync(EnsureOptions(context), cancellationToken);
        var processId = server.ProcessId;
        await server.DisposeAsync();

        await OpenCodeServer.StopAsync(
            new OpenCodeServerStopOptions { RegistrationFilePath = context.RegistrationFile },
            cancellationToken);

        await Assert.That(context.RegistrationFile).Satisfies(path => !FileSystem.File.Exists(path));
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(processId, ExitBound, cancellationToken)).IsTrue()
            .Because("the stopped daemon must leave the process table, reaped by the host that spawned it");
    }

    /// <summary>The Ensure options every direct caller uses: the isolated registration file by path
    /// (the test process's own roots are the developer's, never the isolated ones), the forwarding
    /// shim as the explicit command, and the isolated roots as the spawned service's environment.</summary>
    private static OpenCodeServerEnsureOptions EnsureOptions(EnsureServiceContext context) =>
        new()
        {
            RegistrationFilePath = context.RegistrationFile,
            Command = [context.ShimPath, "serve", "--service"],
            Environment = context.Environment,
        };

    private static async Task<ServiceRegistration> ReadRegistrationAsync(string path) =>
        await ServiceRegistrationReader.TryReadAsync(new TestablyServiceFileSystem(FileSystem), path, CancellationToken.None).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"No usable registration at '{path}'.");

    private static async Task SeedAsync(string path, string document)
    {
        _ = FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(path)!);
        using var stream = FileSystem.FileStream.New(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(document);
        await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
    }
}
