using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.BackgroundService.Discovery;

/// <summary>
/// Exact-pin process evidence for background-service discovery (design §13.1): the accepted
/// pin's own <c>serve --service</c> daemon under isolated roots, found by channel from an isolated
/// process and by registration file in this one; a client that answers health; the
/// <c>ExpectedVersion</c> gate; a handle that owns nothing and leaves the shared service alive on
/// disposal. The environment-reading proofs against a loopback daemon live in
/// <see cref="OpenCodeServerDiscoveryIsolatedProcessTests"/>.
/// </summary>
/// <remarks>
/// Keyless <c>[NotInParallel]</c> rather than the server-process key, the rule research log Q157
/// sets for a test whose assertion depends on a wall-clock bound the host can miss under load:
/// every proof here runs the SDK's own probe, bounded at the pinned two seconds, on this host's
/// thread pool, and a host busy with the rest of the suite can miss that bound even against a
/// daemon in another process (one in-process discovery answered null on the Windows leg with the
/// daemon alive, research log Q172). Running alone after every other test keeps the host quiet
/// while the probes run; the fixture still starts exactly one process.
/// </remarks>
[ClassDataSource<PinnedManagedServiceFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public sealed class OpenCodeServerDiscoveryLiveTests(PinnedManagedServiceFixture service)
{
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
        var probe = new RecordingProbe(new ServiceInfoProbe(ServiceTimingData.Patient, new LoopbackTransport(new LoopbackSocketConnector())));
        var mismatching = await OpenCodeServer.DiscoverWithSeamsAsync(
            new OpenCodeServerDiscoverOptions { RegistrationFilePath = service.RegistrationFile, ExpectedVersion = "0.0.0-never-1" },
            probe,
            cancellationToken);

        await Assert.That(matching).IsNotNull();
        await Assert.That(mismatching).IsNull();
        var verdict = probe.Last;
        await Assert.That(verdict).IsNotNull();
        await Assert.That(verdict!.TimedOut).IsFalse();
        await Assert.That(verdict.IsReadyAndCompatible).IsTrue();
        await Assert.That(verdict.Version).IsEqualTo(service.Version);
    }

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
