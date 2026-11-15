using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Discovery;

/// <summary>The caller-addressed background-service lookup over substituted external seams.</summary>
public sealed class OpenCodeServerFindIncumbentTests
{
    private const string CallerAddress = "http://127.0.0.1:4096";
    private const string Version = "2.0.3";
    private const string OtherVersion = "2.0.2";
    private const string ModernFixture = "BackgroundService.registration-modern.json";
    private const string RelativeAddress = "/api";
    private const string OpaqueAddress = "mailto:owner@example.com";
    private const string Channel = "dev";
    private const int ProcessId = 48213;
    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();
    private readonly IServiceInfoProbe _probe = Substitute.For<IServiceInfoProbe>();

    [Test]
    [Arguments((int)ServiceState.Ready, OpenCodeServerState.Ready)]
    [Arguments((int)ServiceState.Waiting, OpenCodeServerState.Waiting)]
    [Arguments((int)ServiceState.Failed, OpenCodeServerState.Failed)]
    public async Task FindIncumbentWithSeamsAsync_Should_Report_Each_Service_State(int state, OpenCodeServerState expected)
    {
        await SeedRegistrationAsync();
        Answer(new ServiceProbeResult((ServiceState)state, Version, TimedOut: false, Compatible: true));

        var result = await FindAsync();

        await Assert.That(result).IsNotNull();
        await using var handle = result.Server;
        await Assert.That(result.State).IsEqualTo(expected);
        await Assert.That(handle.Endpoint).IsEqualTo(new Uri(CallerAddress));
        await Assert.That(handle.ProcessId).IsEqualTo(ProcessId);
        await Assert.That(handle.Password).IsEqualTo(ServiceRegistrationData.Password);
        await Assert.That(handle.OwnsProcess).IsFalse();
    }

    [Test]
    public async Task FindIncumbentWithSeamsAsync_Should_Probe_The_Caller_Address_Once_With_The_Caller_Token()
    {
        await SeedRegistrationAsync();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));
        using var source = new CancellationTokenSource();

        _ = await FindAsync(cancellationToken: source.Token);

        _ = _probe.Received(1).ProbeAsync(Arg.Any<ServiceRegistration>(), source.Token);
        await Assert.That(_probe.ReceivedCalls().Count()).IsEqualTo(1);
        var registration = ProbedRegistration();
        await Assert.That(registration.Endpoint).IsEqualTo(new Uri(CallerAddress));
        await Assert.That(ServiceRegistrationIdentity.Of(registration)).IsEqualTo(
            new ServiceRegistrationIdentity("srv_01J9Z7K8M4N2P6Q1R3S5T7V9W", Version, CallerAddress, ProcessId));
        await Assert.That(registration.Password).IsEqualTo(ServiceRegistrationData.Password);
    }

    [Test]
    [Arguments("http://0.0.0.0:4096", CallerAddress)]
    [Arguments("http://[::]:4096", "http://[::1]:4096")]
    public async Task FindIncumbentWithSeamsAsync_Should_Connect_Unspecified_Addresses_As_Loopback(string address, string endpoint)
    {
        await SeedRegistrationAsync();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        var result = await FindAsync(new Uri(address));

        await Assert.That(result).IsNotNull();
        await using var handle = result.Server;
        await Assert.That(handle.Endpoint).IsEqualTo(new Uri(endpoint));
        await Assert.That(ProbedRegistration().Endpoint).IsEqualTo(new Uri(endpoint));
        await Assert.That(ProbedRegistration().Url).IsEqualTo(address);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FindIncumbentWithSeamsAsync_Should_Return_Null_Without_Probing_When_No_Registration_Reads(bool malformed)
    {
        if (malformed)
        {
            await SeedRegistrationAsync(ServiceRegistrationData.Malformed);
        }

        await Assert.That(await FindAsync()).IsNull();
        await Assert.That(_probe.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(null, false, true)]
    [Arguments(null, true, true)]
    [Arguments((int)ServiceState.Ready, false, false)]
    public async Task FindIncumbentWithSeamsAsync_Should_Return_Null_When_No_Compatible_Service_Answers(int? state, bool timedOut, bool compatible)
    {
        await SeedRegistrationAsync();
        Answer(new ServiceProbeResult((ServiceState?)state, Version, timedOut, compatible));

        await Assert.That(await FindAsync()).IsNull();
        _ = _probe.Received(1).ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false, (int)ServiceState.Ready, Version, OpenCodeServerState.Ready)]
    [Arguments(false, (int)ServiceState.Waiting, Version, OpenCodeServerState.Waiting)]
    [Arguments(false, (int)ServiceState.Failed, Version, OpenCodeServerState.Failed)]
    [Arguments(false, (int)ServiceState.Ready, OtherVersion, null)]
    [Arguments(false, (int)ServiceState.Waiting, OtherVersion, null)]
    [Arguments(false, (int)ServiceState.Failed, OtherVersion, null)]
    [Arguments(false, (int)ServiceState.Ready, null, OpenCodeServerState.Ready)]
    [Arguments(true, (int)ServiceState.Ready, Version, OpenCodeServerState.Ready)]
    public async Task FindIncumbentWithSeamsAsync_Should_Filter_The_Reported_Version_In_Every_State(
        bool versionless, int state, string? expectedVersion, OpenCodeServerState? expected)
    {
        await SeedRegistrationAsync(versionless ? ServiceRegistrationData.Versionless : null);
        Answer(new ServiceProbeResult((ServiceState)state, Version, TimedOut: false, Compatible: true));

        var result = await FindAsync(options: Options(expectedVersion));

        if (expected is null)
        {
            await Assert.That(result).IsNull();
            return;
        }

        await Assert.That(result).IsNotNull();
        await using var handle = result.Server;
        await Assert.That(result.State).IsEqualTo(expected.Value);
    }

    [Test]
    public async Task FindIncumbentWithSeamsAsync_Should_Use_A_Passwordless_Registration()
    {
        await SeedRegistrationAsync(ServiceRegistrationData.Passwordless);
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        var result = await FindAsync();

        await Assert.That(result).IsNotNull();
        await using var handle = result.Server;
        await Assert.That(handle.Password).IsNull();
        await Assert.That(ProbedRegistration().Password).IsNull();
    }

    [Test]
    [Arguments("ws://127.0.0.1:4096")]
    [Arguments("x-opencode://127.0.0.1:4096")]
    [Arguments("x-opencode:/a")]
    [Arguments("x-opencode:\t/a")]
    [Arguments(" x-opencode:/a ")]
    public async Task FindIncumbentWithSeamsAsync_Should_Return_Null_Without_Probing_For_A_Non_Http_Base_Url(string address)
    {
        await SeedRegistrationAsync();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        await Assert.That(await FindAsync(new Uri(address, UriKind.Absolute))).IsNull();
        await Assert.That(_probe.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FindIncumbentWithSeamsAsync_Should_Validate_Options_Before_Cancellation_Or_Access(bool cancelled)
    {
        var files = Substitute.For<IServiceFileSystem>();
        var options = Options();
        options.Channel = Channel;

        var exception = await Assert.That(() => FindAsync(options: options,
            cancellationToken: new CancellationToken(cancelled), files: files)).ThrowsExactly<ArgumentException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception.ParamName).IsEqualTo("options");
        await Assert.That(files.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
        await Assert.That(_probe.ReceivedCalls()).IsEmpty();
    }

    [Test]
    public async Task FindIncumbentWithSeamsAsync_Should_Propagate_The_Probe_Cancellation_Instance()
    {
        await SeedRegistrationAsync();
        using var source = new CancellationTokenSource();
        OperationCanceledException? expected = null;
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns<Task<ServiceProbeResult>>(async call =>
        {
            await source.CancelOnWorkerAsync();
            throw expected = new OperationCanceledException(call.Arg<CancellationToken>());
        });

        var exception = await Assert.That(() => FindAsync(cancellationToken: source.Token)).ThrowsExactly<OperationCanceledException>();

        await Assert.That(exception).IsSameReferenceAs(expected);
    }

    [Test]
    public async Task FindIncumbentAsync_Should_Refuse_A_Null_Url()
    {
        var exception = await Assert.That(() => OpenCodeServer.FindIncumbentAsync(null!)).ThrowsExactly<ArgumentNullException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception.ParamName).IsEqualTo("url");
    }

    [Test]
    [Arguments(RelativeAddress, UriKind.Relative)]
    [Arguments(OpaqueAddress, UriKind.Absolute)]
    public async Task FindIncumbentWithSeamsAsync_Should_Refuse_A_Non_Base_Url_After_Reading_A_Registration(string address, UriKind kind)
    {
        await SeedRegistrationAsync();
        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));

        var exception = await Assert.That(() => FindAsync(new Uri(address, kind))).ThrowsExactly<ArgumentException>();

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception.ParamName).IsEqualTo("url");
        await Assert.That(_probe.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(RelativeAddress, UriKind.Relative)]
    [Arguments(OpaqueAddress, UriKind.Absolute)]
    public async Task FindIncumbentWithSeamsAsync_Should_Return_Null_For_A_Non_Base_Url_When_No_Registration_Reads(string address, UriKind kind)
    {
        await Assert.That(await FindAsync(new Uri(address, kind))).IsNull();
        await Assert.That(_probe.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FindIncumbentWithSeamsAsync_Should_Pass_The_Caller_Token_To_Registration_And_Migration_Reads(bool channel)
    {
        var files = Substitute.For<IServiceFileSystem>();
        var options = channel ? new OpenCodeServerDiscoverOptions { Channel = Channel } : Options();
        var paths = PathsFor(options);
        files.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        if (!channel)
        {
            files.TryReadAllBytesAsync(paths.RegistrationFile, Arg.Any<CancellationToken>())
                .Returns(Encoding.UTF8.GetBytes(new FixtureLoader().LoadJson(ModernFixture)));
        }

        Answer(new ServiceProbeResult(ServiceState.Ready, Version, TimedOut: false, Compatible: true));
        using var source = new CancellationTokenSource();
        var result = await FindAsync(options: options, cancellationToken: source.Token, files: files);

        if (channel)
        {
            await Assert.That(result).IsNull();
        }
        else
        {
            await Assert.That(result).IsNotNull();
        }

        await using var handle = result?.Server;
        string[] expectedPaths = channel
            ? [.. paths.LegacyRegistrationFiles, .. new[] { paths.LegacyConfigFile }.OfType<string>(), paths.RegistrationFile]
            : [paths.RegistrationFile];
        await Assert.That(files.ReceivedCalls().Select(static call => (string)call.GetArguments()[0]!).ToArray())
            .IsEquivalentTo(expectedPaths, CollectionOrdering.Matching);
        foreach (var path in expectedPaths)
        {
            _ = files.Received(1).TryReadAllBytesAsync(path, source.Token);
        }

        _ = files.DidNotReceive().TryReadAllBytesAsync(Arg.Any<string>(), Arg.Is<CancellationToken>(token => token != source.Token));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FindIncumbentWithSeamsAsync_Should_Propagate_The_Seam_Read_Cancellation_Instance(bool channel)
    {
        var files = Substitute.For<IServiceFileSystem>();
        var options = channel ? new OpenCodeServerDiscoverOptions { Channel = Channel } : Options();
        var paths = PathsFor(options);
        using var source = new CancellationTokenSource();
        var expected = new OperationCanceledException("seam read", source.Token);
        files.TryReadAllBytesAsync(channel ? paths.LegacyRegistrationFiles[0] : paths.RegistrationFile, Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw expected);

        var exception = await Assert.That(() => FindAsync(options: options, cancellationToken: source.Token, files: files))
            .ThrowsExactly<OperationCanceledException>();

        await Assert.That(exception).IsSameReferenceAs(expected);
        await Assert.That(_probe.ReceivedCalls()).IsEmpty();
    }

    private string RegistrationPath => _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "incumbent", "service.json");

    private OpenCodeServerDiscoverOptions Options(string? expectedVersion = null) =>
        new() { RegistrationFilePath = RegistrationPath, ExpectedVersion = expectedVersion };

    private Task<OpenCodeServerIncumbent?> FindAsync(
        Uri? url = null, OpenCodeServerDiscoverOptions? options = null,
        IServiceFileSystem? files = null, CancellationToken cancellationToken = default) =>
        OpenCodeServer.FindIncumbentWithSeamsAsync(url ?? new Uri(CallerAddress), options ?? Options(),
            new ServiceDiscovery(_environment, files ?? new TestablyServiceFileSystem(_fileSystem), _probe), cancellationToken);

    private void Answer(ServiceProbeResult answer) =>
        _probe.ProbeAsync(Arg.Any<ServiceRegistration>(), Arg.Any<CancellationToken>()).Returns(answer);

    private ServiceRegistration ProbedRegistration() => (ServiceRegistration)_probe.ReceivedCalls().Single().GetArguments()[0]!;

    private ServicePaths PathsFor(OpenCodeServerDiscoverOptions options)
    {
        if (options.Channel is not null)
        {
            _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(_fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "state"));
            _environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(_fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "config"));
        }

        return new ServicePathResolver(_environment).Resolve(ServiceSelection.Snapshot(options));
    }

    private async Task SeedRegistrationAsync(string? document = null)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(RegistrationPath)!);
        await Assert.That(await new TestablyServiceFileSystem(_fileSystem).TryCreateExclusiveAsync(RegistrationPath,
            Encoding.UTF8.GetBytes(document ?? new FixtureLoader().LoadJson(ModernFixture)), CancellationToken.None)).IsTrue();
    }
}
