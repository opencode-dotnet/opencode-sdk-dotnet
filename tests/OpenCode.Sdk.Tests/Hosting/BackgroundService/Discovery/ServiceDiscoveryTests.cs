using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Discovery;

/// <summary>
/// The discovery algorithm over substituted seams: the environment, the filesystem double, and a
/// scripted probe. No socket, no real file; the pinned client's <c>discover</c> decisions only.
/// </summary>
public sealed class ServiceDiscoveryTests
{
    private const string Version = "2.0.3";

    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();
    private readonly IServiceInfoProbe _probe = Substitute.For<IServiceInfoProbe>();

    public ServiceDiscoveryTests()
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(Root("state"));
        _environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(Root("config"));
    }

    [Test]
    public async Task DiscoverAsync_Should_Return_Null_When_The_Registration_Is_Missing()
    {
        var registration = await Discovery().DiscoverAsync(options: null, CancellationToken.None);

        await Assert.That(registration).IsNull();
        _ = _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
    }

    [Test]
    [Arguments(ServiceRegistrationData.Passwordless)]
    [Arguments(ServiceRegistrationData.Malformed)]
    public async Task DiscoverAsync_Should_Return_Null_Without_Probing_An_Unusable_Registration(string document)
    {
        Seed(SharedRegistrationPath(), document);

        var registration = await Discovery().DiscoverAsync(options: null, CancellationToken.None);

        await Assert.That(registration).IsNull();
        _ = _probe.DidNotReceiveWithAnyArgs().ProbeAsync(default!, default);
    }

    [Test]
    [Arguments((int)ServiceState.Waiting)]
    [Arguments((int)ServiceState.Failed)]
    public async Task DiscoverAsync_Should_Return_Null_When_The_Daemon_Is_Not_Ready(int state)
    {
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));
        Answer(new ServiceProbeResult((ServiceState)state, Version, TimedOut: false, Compatible: true));

        var registration = await Discovery().DiscoverAsync(options: null, CancellationToken.None);

        await Assert.That(registration).IsNull();
    }

    [Test]
    public async Task DiscoverAsync_Should_Return_Null_When_The_Service_Is_Incompatible()
    {
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: false));

        var registration = await Discovery().DiscoverAsync(options: null, CancellationToken.None);

        await Assert.That(registration).IsNull();
    }

    [Test]
    public async Task DiscoverAsync_Should_Return_Null_When_The_Probe_Timed_Out()
    {
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));
        Answer(new ServiceProbeResult(State: null, Version: null, TimedOut: true, Compatible: true));

        var registration = await Discovery().DiscoverAsync(options: null, CancellationToken.None);

        await Assert.That(registration).IsNull();
    }

    [Test]
    public async Task DiscoverAsync_Should_Return_Null_When_ExpectedVersion_Differs()
    {
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        var registration = await Discovery().DiscoverAsync(new OpenCodeServerDiscoverOptions { ExpectedVersion = "2.0.2" }, CancellationToken.None);

        await Assert.That(registration).IsNull();
    }

    [Test]
    public async Task DiscoverAsync_Should_Return_The_Identity_Of_A_Ready_Service()
    {
        Seed(SharedRegistrationPath(), new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        var registration = await Discovery().DiscoverAsync(new OpenCodeServerDiscoverOptions { ExpectedVersion = Version }, CancellationToken.None);

        await Assert.That(registration).IsNotNull();
        await Assert.That(registration.Endpoint).IsEqualTo(new Uri("http://127.0.0.1:49374"));
        await Assert.That(registration.ProcessId).IsEqualTo(48213);
        await Assert.That(registration.Password).IsEqualTo(ServiceRegistrationData.Password);
    }

    [Test]
    public async Task DiscoverAsync_Should_Read_A_Direct_Registration_File_Without_The_Environment()
    {
        var direct = Path("elsewhere", "registration.json");
        Seed(direct, new FixtureLoader().LoadJson("BackgroundService.registration-modern.json"));
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        var registration = await Discovery().DiscoverAsync(new OpenCodeServerDiscoverOptions { RegistrationFilePath = direct }, CancellationToken.None);

        await Assert.That(registration).IsNotNull();
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task DiscoverAsync_Should_Run_The_Migration_Before_Reading_In_Channel_Mode()
    {
        // A dev build registered under the hashed legacy name and nothing under the current one:
        // the copy the CLI would make is what discovery reads.
        var legacy = _fileSystem.Path.Combine(Root("state"), "opencode", ServiceLegacyFilename.For("dev"));
        Seed(legacy, ServiceRegistrationData.DevPrerelease);
        Answer(new ServiceProbeResult(ServiceState.Ready, "0.0.0-dev-19646", TimedOut: false, Compatible: true));

        var registration = await Discovery().DiscoverAsync(new OpenCodeServerDiscoverOptions { Channel = "dev" }, CancellationToken.None);

        await Assert.That(registration).IsNotNull();
        await Assert.That(_fileSystem.File.Exists(SharedRegistrationPath())).IsTrue();
    }

    [Test]
    public async Task DiscoverAsync_Should_Refuse_Contradictory_Options_Before_Touching_Anything()
    {
        var options = new OpenCodeServerDiscoverOptions { Channel = "dev", RegistrationFilePath = Path("elsewhere", "registration.json") };

        var exception = await Assert
            .That(async () => _ = await Discovery().DiscoverAsync(options, CancellationToken.None))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
        _ = _environment.DidNotReceiveWithAnyArgs().GetEnvironmentVariable(default!);
    }

    [Test]
    public async Task DiscoverAsync_Should_Throw_When_No_Home_Resolves_For_The_Fallback()
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns((string?)null);
        _environment.UserProfile.Returns((string?)null);

        _ = await Assert
            .That(async () => _ = await Discovery().DiscoverAsync(options: null, CancellationToken.None))
            .Throws<OpenCodeServerException>();
    }

    [Test]
    public async Task DiscoverAsync_Should_Rethrow_Caller_Cancellation()
    {
        var cancelled = new CancellationToken(canceled: true);

        _ = await Assert
            .That(async () => _ = await Discovery().DiscoverAsync(options: null, cancelled))
            .Throws<OperationCanceledException>();
    }

    private ServiceDiscovery Discovery() =>
        new(_environment, new TestablyServiceFileSystem(_fileSystem), _probe);

    private void Answer(ServiceProbeResult result) =>
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(result);

    private string SharedRegistrationPath() => _fileSystem.Path.Combine(Root("state"), "opencode", "service.json");

    private void Seed(string path, string content)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        _fileSystem.File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private string Root(string name) => Path(name);

    private string Path(params string[] segments) =>
        _fileSystem.Path.Combine([_fileSystem.Path.GetTempPath(), "service-discovery", .. segments]);
}
