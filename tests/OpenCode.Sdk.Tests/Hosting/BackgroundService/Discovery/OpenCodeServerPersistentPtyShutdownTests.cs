using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Discovery;

/// <summary>The discovery-and-shutdown composition over the canonical filesystem double and substituted seams.</summary>
public sealed class OpenCodeServerPersistentPtyShutdownTests
{
    private const string Version = "2.0.3";
    private const string RefusedMessage = "The service refused the persistent-terminal shutdown.";
    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();
    private readonly IServiceInfoProbe _probe = Substitute.For<IServiceInfoProbe>();
    private readonly IServicePtyShutdown _shutdown = Substitute.For<IServicePtyShutdown>();

    [Test]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Complete_Without_Probing_Or_Shutting_Down_When_The_Registration_Is_Missing()
    {
        await InvokeAsync(Options(), CancellationToken.None);

        _ = _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
        _ = _shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
    }

    [Test]
    [Arguments((int)ServiceState.Waiting, true)]
    [Arguments((int)ServiceState.Failed, true)]
    [Arguments((int)ServiceState.Ready, false)]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Send_No_Shutdown_When_The_Service_Is_Not_Ready_Or_Compatible(int state, bool compatible)
    {
        await SeedRegistrationAsync();
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceProbeResult((ServiceState)state, Version, TimedOut: false, Compatible: compatible));

        await InvokeAsync(Options(), CancellationToken.None);

        _ = _probe.Received(1).ProbeAsync(Arg.Any<ServiceRegistration>(), CancellationToken.None);
        _ = _shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
    }

    [Test]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Shut_Down_The_Discovered_Service_With_The_Caller_Token()
    {
        await ReadyServiceAsync();
        using var caller = new CancellationTokenSource();

        await InvokeAsync(Options(), caller.Token);

        var call = _shutdown.ReceivedCalls().Single();
        var registration = (ServiceRegistration)call.GetArguments()[0]!;
        await Assert.That(registration.Endpoint).IsEqualTo(new Uri("http://127.0.0.1:49374"));
        await Assert.That(registration.ProcessId).IsEqualTo(48213);
        await Assert.That(registration.Password).IsEqualTo(ServiceRegistrationData.Password);
        await Assert.That((CancellationToken)call.GetArguments()[1]!).IsEqualTo(caller.Token);
        _ = _shutdown.Received(1).ShutdownAsync(registration, caller.Token);
        _ = _probe.Received(1).ProbeAsync(registration, caller.Token);
    }

    [Test]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Propagate_The_Shutdown_Api_Exception_Unchanged()
    {
        await ReadyServiceAsync();
        var refused = new OpenCodeApiException(RefusedMessage, 401, error: null, rawBody: null);
        _shutdown.ShutdownAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(refused));

        var failure = await Assert.That(() => InvokeAsync(Options(), CancellationToken.None)).Throws<OpenCodeApiException>();

        await Assert.That(failure).IsSameReferenceAs(refused);
    }

    [Test]
    [Arguments("2.0.2", 0)]
    [Arguments(Version, 1)]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Apply_ExpectedVersion(string expectedVersion, int shutdownCount)
    {
        await ReadyServiceAsync();
        var options = Options();
        options.ExpectedVersion = expectedVersion;

        await InvokeAsync(options, CancellationToken.None);

        _ = _shutdown.Received(shutdownCount).ShutdownAsync(Arg.Any<ServiceRegistration>(), CancellationToken.None);
    }

    [Test]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Refuse_Contradictory_Options_Before_Probing_Or_Shutting_Down()
    {
        var options = Options();
        options.Channel = "dev";

        var failure = await Assert.That(() => InvokeAsync(options, CancellationToken.None)).Throws<ArgumentException>();

        await Assert.That(failure!.ParamName).IsEqualTo("options");
        _ = _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
        _ = _shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
    }

    [Test]
    public async Task ShutdownPersistentPtyWithSeamsAsync_Should_Propagate_Probe_Cancellation_Unchanged()
    {
        await SeedRegistrationAsync();
        using var caller = new CancellationTokenSource();
        OperationCanceledException? cancelled = null;
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns<Task<ServiceProbeResult>>(async call =>
        {
            await caller.CancelOnWorkerAsync();
            cancelled = new OperationCanceledException(call.Arg<CancellationToken>());
            throw cancelled;
        });

        var failure = await Assert.That(() => InvokeAsync(Options(), caller.Token)).Throws<OperationCanceledException>();

        await Assert.That(failure).IsSameReferenceAs(cancelled);
        await Assert.That(failure!.CancellationToken).IsEqualTo(caller.Token);
        _ = _shutdown.DidNotReceiveWithAnyArgs().ShutdownAsync(default!, default);
    }

    private Task InvokeAsync(OpenCodeServerDiscoverOptions options, CancellationToken cancellationToken) =>
        OpenCodeServer.ShutdownPersistentPtyWithSeamsAsync(options,
            new ServiceDiscovery(_environment, new TestablyServiceFileSystem(_fileSystem), _probe), _shutdown, cancellationToken);

    private OpenCodeServerDiscoverOptions Options() => new()
    {
        RegistrationFilePath = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "pty-shutdown", "service.json"),
    };

    private async Task SeedRegistrationAsync()
    {
        var path = Options().RegistrationFilePath!;
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        var document = new FixtureLoader().LoadJson("BackgroundService.registration-modern.json");
        await Assert.That(await new TestablyServiceFileSystem(_fileSystem)
            .TryCreateExclusiveAsync(path, Encoding.UTF8.GetBytes(document), CancellationToken.None)).IsTrue();
    }

    private async Task ReadyServiceAsync()
    {
        await SeedRegistrationAsync();
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>())
            .Returns(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));
    }
}
