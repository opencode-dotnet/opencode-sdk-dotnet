using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using OpenCode.Sdk.Tests.PersistentPtys;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Discovery;

/// <summary>
/// Exact-pin process evidence for background-service discovery: the accepted
/// pin's own <c>serve --service</c> daemon under isolated roots, found by channel from an isolated
/// process and by registration file in this one; a client that answers health; the
/// <c>ExpectedVersion</c> gate; a handle that owns nothing and leaves the shared service alive on
/// disposal. The environment-reading proofs against a loopback daemon live in
/// <see cref="OpenCodeServerDiscoveryIsolatedProcessTests"/>.
/// </summary>
/// <remarks>
/// Keyless <c>[NotInParallel]</c> rather than the server-process key, the rule for a test whose
/// assertion depends on a wall-clock bound the host can miss under load:
/// every proof here runs the SDK's own probe, bounded at the pinned two seconds, on this host's
/// thread pool, and a host busy with the rest of the suite can miss that bound even against a
/// daemon in another process (one in-process discovery answered null on the Windows leg with the
/// daemon alive). Running alone after every other test keeps the host quiet
/// while the probes run; the fixture still starts exactly one process.
/// </remarks>
[ClassDataSource<PinnedManagedServiceFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public sealed class OpenCodeServerDiscoveryLiveTests(PinnedManagedServiceFixture service)
{
    private const string ShutdownSessionTitle = "background service persistent terminal shutdown";
    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(120_000)]
    public async Task DiscoverAsync_Should_Find_The_Accepted_Pin_Managed_Service_By_Channel(CancellationToken cancellationToken)
    {
        // The same run-root environment the service received, from a process that has no other
        // way to find it: what a consumer on this machine would see, minus the developer profile.
        var environment = service.Environment.ToDictionary(
            static pair => pair.Key, static pair => (string?)pair.Value, StringComparer.Ordinal);

        var result = await new ServiceFixtureCommand(FileSystem)
            .RunAsync(["discover-channel", PinnedManagedServiceFixture.Channel], environment, cancellationToken);

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StandardError);
        await Assert.That(result.StandardOutput.Trim())
            .IsEqualTo(ServiceFixtureOutput.FoundLine(service.ProcessId, service.Endpoint))
            .Because(result.StandardError);
    }

    [Test]
    [Timeout(120_000)]
    public async Task DiscoverAsync_Should_Find_The_Managed_Service_By_Registration_File(CancellationToken cancellationToken)
    {
        var server = await DiscoverByFileAsync(cancellationToken);
        await using var _ = server;

        await Assert.That(server.OwnsProcess).IsFalse();
        await Assert.That(server.ProcessId).IsEqualTo(service.ProcessId);
        await Assert.That(server.Endpoint).IsEqualTo(service.Endpoint);
        await Assert.That(server.Username).IsEqualTo("opencode");
    }

    [Test]
    [Timeout(120_000)]
    public async Task Discovered_Server_Should_Create_A_Client_That_Answers_Health(CancellationToken cancellationToken)
    {
        var server = await DiscoverByFileAsync(cancellationToken);
        await using var _ = server;

        using var client = server.CreateClient();
        var health = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

        await Assert.That(health.Status).IsEqualTo(200);
        await Assert.That(health.ServerInfo.Pid).IsEqualTo(server.ProcessId);
        await Assert.That(health.ServerInfo.Version).IsEqualTo(service.Version);
    }

    [Test]
    [Timeout(120_000)]
    public async Task Disposing_Discovered_Server_Should_Leave_The_Shared_Service_Alive(CancellationToken cancellationToken)
    {
        var server = await DiscoverByFileAsync(cancellationToken);

        await server.DisposeAsync();
        // A second disposal is a no-op by contract; this is the idempotence proof.
        await server.DisposeAsync();

        await Assert.That(ProcessObservation.IsRunning(service.ProcessId)).IsTrue();
        var again = await DiscoverByFileAsync(cancellationToken);
        await using var _ = again;
        using var client = again.CreateClient();
        var health = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);
        await Assert.That(health.ServerInfo.Pid).IsEqualTo(service.ProcessId);
    }

    /// <summary>
    /// Discovery answers null alike for a version mismatch and for a probe whose bound expired, so
    /// the mismatch runs through the internal seam over the platform probe and is proved by the
    /// probe's own verdict: the daemon answered ready, in time, with a version other than the one
    /// expected. The claim is the version gate, not the two-second bound, so the probe gets a
    /// patient bound a loaded host does not reach; the matching call stays on the public door.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DiscoverAsync_Should_Apply_ExpectedVersion_Against_The_Accepted_Server(CancellationToken cancellationToken)
    {
        var matching = await OpenCodeServer.DiscoverAsync(
            new OpenCodeServerDiscoverOptions { RegistrationFilePath = service.RegistrationFile, ExpectedVersion = service.Version },
            cancellationToken);
        await using var _ = matching;
        var probe = new RecordingProbe(PatientProbe());
        var mismatching = await OpenCodeServer.DiscoverWithSeamsAsync(
            new OpenCodeServerDiscoverOptions { RegistrationFilePath = service.RegistrationFile, ExpectedVersion = "0.0.0-never-1" },
            probe,
            cancellationToken);

        await Assert.That(matching).IsNotNull();
        await Assert.That(mismatching).IsNull();
        await AssertReadyVerdictAsync(probe);
    }

    [Test]
    [Timeout(120_000)]
    public async Task ShutdownPersistentPtyAsync_Should_End_The_Service_Terminals_Or_Answer_The_Daemon_Absent_Arm(CancellationToken cancellationToken)
    {
        var options = new OpenCodeServerDiscoverOptions { RegistrationFilePath = service.RegistrationFile };
        var probe = PatientProbe();
        await using var handle = await OpenCodeServer.DiscoverWithSeamsAsync(options, probe, cancellationToken);
        await Assert.That(handle).IsNotNull();
        using var client = handle.CreateClient();
        var session = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = ShutdownSessionTitle,
                Location = new LocationPublicRef { Directory = service.RunRoot },
            }, cancellationToken: cancellationToken);
        var created = await client.PersistentPtys.CreatePersistentPtyAsync(
            session.Session.Id, PersistentPtyLiveTests.CreateRequest(), OpenCodeRequestOptions.NoThrow, cancellationToken);
        var before = await client.PersistentPtys.ListPersistentPtysAsync(session.Session.Id, cancellationToken: cancellationToken);
        if (PersistentPtyDaemonGate.DaemonExpected)
        {
            await Assert.That(created.Status).IsEqualTo(200);
            await Assert.That(before.PersistentPtys.Select(static pty => pty.Id).ToArray()).Contains(created.PersistentPty.Id);
        }
        else
        {
            await Assert.That(created.Status).IsEqualTo(503);
            await Assert.That(created.Error).IsTypeOf<ServiceUnavailableError>();
            await Assert.That(((ServiceUnavailableError)created.Error!).Service).IsEqualTo(PersistentPtyLiveTests.DaemonService);
        }

        var shutdownProbe = new RecordingProbe(PatientProbe());
        var shutdown = new RecordingShutdown(new ServicePtyShutdown());
        await OpenCodeServer.ShutdownPersistentPtyWithSeamsAsync(options,
            new ServiceDiscovery(new ServiceEnvironment(), new ServiceFileSystem(), shutdownProbe), shutdown, cancellationToken);

        var verdict = shutdownProbe.Last;
        await Assert.That(verdict).IsNotNull();
        await Assert.That(verdict!.TimedOut).IsFalse();
        await Assert.That(verdict.IsReadyAndCompatible).IsTrue();
        await Assert.That(shutdown.Calls).IsEqualTo(1);
        var after = await client.PersistentPtys.ListPersistentPtysAsync(
            session.Session.Id, OpenCodeRequestOptions.NoThrow, cancellationToken);
        await Assert.That(after.Status).IsEqualTo(200).Because("Server answer: " + after.RawBody);
        if (PersistentPtyDaemonGate.DaemonExpected)
        {
            await Assert.That(after.PersistentPtys.Select(static pty => pty.Id).ToArray()).DoesNotContain(created.PersistentPty.Id);
        }
        else
        {
            await Assert.That(after.PersistentPtys).IsEmpty();
        }

        Console.WriteLine($"ppty-shutdown-live: arm={(PersistentPtyDaemonGate.DaemonExpected ? "daemon" : "daemon-absent")} " +
            $"create={created.Status.ToString(CultureInfo.InvariantCulture)} before-status={before.Status.ToString(CultureInfo.InvariantCulture)} " +
            $"before-ids={string.Join(',', before.PersistentPtys.Select(static pty => pty.Id))} " +
            $"after-status={after.Status.ToString(CultureInfo.InvariantCulture)} " +
            $"after-ids={string.Join(',', after.PersistentPtys.Select(static pty => pty.Id))}");
    }

    [Test]
    [Timeout(120_000)]
    public async Task FindIncumbentAsync_Should_Report_The_Managed_Service_Ready_At_Its_Url(CancellationToken cancellationToken)
    {
        var probe = new RecordingProbe(PatientProbe());
        var result = await OpenCodeServer.FindIncumbentWithSeamsAsync(service.Endpoint,
            new OpenCodeServerDiscoverOptions { RegistrationFilePath = service.RegistrationFile },
            new ServiceDiscovery(new ServiceEnvironment(), new ServiceFileSystem(), probe), cancellationToken);

        await AssertReadyVerdictAsync(probe);
        await Assert.That(result).IsNotNull();
        await using var handle = result.Server;
        await Assert.That(result.State).IsEqualTo(OpenCodeServerState.Ready);
        await Assert.That(handle.Endpoint).IsEqualTo(service.Endpoint);
        await Assert.That(handle.ProcessId).IsEqualTo(service.ProcessId);
        await Assert.That(handle.OwnsProcess).IsFalse();
        using var client = handle.CreateClient();
        var info = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);
        await Assert.That(info.Status).IsEqualTo(200);
        await Assert.That(info.ServerInfo.Pid).IsEqualTo(service.ProcessId);
        Console.WriteLine($"incumbent-live: {probe.Last}");
    }

    private async Task AssertReadyVerdictAsync(RecordingProbe probe)
    {
        var verdict = probe.Last;
        await Assert.That(verdict).IsNotNull();
        await Assert.That(verdict!.TimedOut).IsFalse();
        await Assert.That(verdict.IsReadyAndCompatible).IsTrue();
        await Assert.That(verdict.Version).IsEqualTo(service.Version);
    }

    private static ServiceInfoProbe PatientProbe() =>
        new(ServiceTimingData.Patient, new LoopbackTransport(new LoopbackSocketConnector()), TimeProvider.System);

    private async Task<OpenCodeServer> DiscoverByFileAsync(CancellationToken cancellationToken)
    {
        var server = await OpenCodeServer.DiscoverAsync(
            new OpenCodeServerDiscoverOptions { RegistrationFilePath = service.RegistrationFile },
            cancellationToken);
        if (server is not null)
        {
            return server;
        }

        // A null against a daemon the fixture reported ready is either the daemon or the bound;
        // a patient probe right now says which.
        var evidence = await service.DescribeHealthAsync(cancellationToken);
        throw new InvalidOperationException(
            $"The managed service registered at '{service.RegistrationFile}' was not discovered although the fixture reported it ready. {evidence}");
    }

    /// <summary>The shipped shutdown, counting the calls forwarded to it.</summary>
    private sealed class RecordingShutdown(IServicePtyShutdown inner) : IServicePtyShutdown
    {
        public int Calls { get; private set; }

        public async Task ShutdownAsync(ServiceRegistration registration, CancellationToken cancellationToken)
        {
            Calls++;
            await inner.ShutdownAsync(registration, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The platform probe, remembering the last verdict it gave discovery.</summary>
    private sealed class RecordingProbe(IServiceInfoProbe inner) : IServiceInfoProbe
    {
        public ServiceProbeResult? Last { get; private set; }

        public async Task<ServiceProbeResult> ProbeAsync(ServiceRegistration registration, CancellationToken cancellationToken)
        {
            Last = await inner.ProbeAsync(registration, cancellationToken).ConfigureAwait(false);
            return Last;
        }
    }
}
