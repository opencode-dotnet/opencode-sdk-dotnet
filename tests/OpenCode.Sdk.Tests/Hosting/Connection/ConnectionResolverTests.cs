using System.Net;
using System.Text.Json.Nodes;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Connection;

public sealed class ConnectionResolverTests : IDisposable
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:4096");
    private readonly MockFileSystem _mock = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();
    private readonly IServiceFileSystem _files = Substitute.For<IServiceFileSystem>();
    private readonly IServerDoors _doors = Substitute.For<IServerDoors>();
    private readonly List<string> _reads = [];
    private readonly RecordingHttpHandler _handler;
    private readonly HttpClient _transport;
    private readonly ConnectionSeams _seams;

    public ConnectionResolverTests()
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(_mock.Path.Combine(_mock.Path.GetTempPath(), "resolve-state"));
        _environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(_mock.Path.Combine(_mock.Path.GetTempPath(), "resolve-config"));
        var adapter = new TestablyServiceFileSystem(_mock);
        _files.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var path = call.Arg<string>();
            _reads.Add(path);
            return adapter.TryReadAllBytesAsync(path, call.Arg<CancellationToken>());
        });
        _files.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => adapter.TryCreateExclusiveAsync(call.Arg<string>(), call.Arg<ReadOnlyMemory<byte>>(), call.Arg<CancellationToken>()));
        _doors.StartAsync(Arg.Any<OpenCodeServerOptions?>(), Arg.Any<CancellationToken>()).Returns(_ => Shared(null));
        _doors.DiscoverLocatedAsync(Arg.Any<ServicePaths>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((OpenCodeServer?)null);
        _doors.EnsureLocatedAsync(Arg.Any<EnsureRequest>(), Arg.Any<ServicePaths>(), Arg.Any<ServiceConfigEnvironment>(), Arg.Any<CancellationToken>())
            .Returns(_ => Shared("2.2.7"));
        _handler = new RecordingHttpHandler(_ => InfoReply("2.3.0"));
        _transport = new HttpClient(_handler, disposeHandler: false);
        _seams = new ConnectionSeams(_doors, _environment, _files, _transport, new ManualTimeProvider());
        _environment.ClearReceivedCalls();
    }

    public void Dispose()
    {
        _transport.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task ResolveAsync_Should_Return_An_Explicit_Connection_When_The_Version_Matches()
    {
        var contexts = new List<OpenCodeServerVersionMismatchContext>();
        await using var connection = await ResolveAsync(new() { Endpoint = Endpoint, Password = "pw", OnServerVersionMismatch = contexts.Add });

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Explicit);
        await Assert.That(connection.Endpoint).IsEqualTo(Endpoint);
        await Assert.That(connection.Username).IsEqualTo("opencode");
        await Assert.That(connection.Password).IsEqualTo("pw");
        await Assert.That(connection.ServerVersion).IsEqualTo("2.3.0");
        await Assert.That(connection.Server).IsNull();
        await Assert.That(connection.ServiceOperations).IsNull();
        await Assert.That(contexts).IsEmpty();
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
        await Assert.That(_files.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments("2.2.9")]
    [Arguments("v2.3.0")]
    [Arguments("2.3.0 ")]
    [Arguments("2.3.00")]
    public async Task ResolveAsync_Should_Report_A_Differing_Version_Once_And_Continue(string version)
    {
        using var handler = new RecordingHttpHandler(_ => InfoReply(version));
        using var transport = new HttpClient(handler);
        var seams = _seams with { ProbeTransport = transport };
        var contexts = new List<OpenCodeServerVersionMismatchContext>();
        var resolving = OpenCodeServer.ResolveWithSeamsAsync(
            new() { Endpoint = Endpoint, OnServerVersionMismatch = contexts.Add }, seams, CancellationToken.None);
        await Assert.That(resolving).ThrowsNothing();
        await using var connection = await resolving;

        await Assert.That(contexts.Count).IsEqualTo(1);
        await Assert.That(contexts[0].Endpoint).IsEqualTo(Endpoint);
        await Assert.That(contexts[0].ServerVersion).IsEqualTo(version);
        await Assert.That(contexts[0].ClientVersion).IsEqualTo("2.3.0");
        await Assert.That(connection.ServerVersion).IsEqualTo(version);
        await using var silent = await OpenCodeServer.ResolveWithSeamsAsync(new() { Endpoint = Endpoint }, seams, CancellationToken.None);
        await Assert.That(silent.ServerVersion).IsEqualTo(version);
    }

    [Test]
    public async Task ResolveAsync_Should_Not_Validate_Unused_Service_Options_In_Explicit_Mode()
    {
        var resolving = ResolveAsync(new()
        {
            Endpoint = Endpoint,
            Launch = new OpenCodeServerOptions(),
            Service = new OpenCodeServerEnsureOptions { VersionPolicy = OpenCodeServerVersionPolicy.Replace },
        });
        await Assert.That(resolving).ThrowsNothing();
        await using var connection = await resolving;

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Explicit);
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveAsync_Should_Start_A_Standalone_Server_Without_Reading_The_Config(bool autostart)
    {
        var launch = new OpenCodeServerOptions();
        await using var started = Shared(null);
        _doors.StartAsync(launch, Arg.Any<CancellationToken>()).Returns(started);
        await using var connection = await ResolveAsync(new() { Standalone = true, Launch = launch, Autostart = autostart });

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Standalone);
        await Assert.That(connection.Server).IsSameReferenceAs(started);
        await Assert.That(connection.ServerVersion).IsNull();
        await Assert.That(connection.ServiceOperations).IsNull();
        await AssertCallsAsync("StartAsync");
        _ = _doors.Received(1).StartAsync(launch, Arg.Any<CancellationToken>());
        await Assert.That(_reads).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveAsync_Should_Start_A_Standalone_Server_When_The_Service_Is_Disabled(bool autostart)
    {
        var service = new OpenCodeServerEnsureOptions { Channel = "dev" };
        var paths = Paths(service);
        Seed(paths.ConfigFile!, ServiceConfigData.DisabledAsTheCliWritesIt);

        await using var connection = await ResolveAsync(new() { Service = service, Autostart = autostart });

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Standalone);
        await Assert.That(connection.ServerVersion).IsNull();
        await Assert.That(connection.ServiceOperations).IsNull();
        await AssertCallsAsync("StartAsync");
        await Assert.That(_reads).IsEquivalentTo([paths.LegacyConfigFile!, paths.ConfigFile!], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolveAsync_Should_Propagate_A_Failed_Standalone_Start_Unchanged()
    {
        var cause = new OpenCodeServerException("start failed");
        _doors.StartAsync(Arg.Any<OpenCodeServerOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<OpenCodeServer>(cause));

        var failure = await Assert.That(async () => _ = await ResolveAsync(new() { Standalone = true })).Throws<OpenCodeServerException>();

        await Assert.That(failure).IsSameReferenceAs(cause);
    }

    [Test]
    [Arguments(OpenCodeServerVersionPolicy.Ignore)]
    [Arguments(OpenCodeServerVersionPolicy.Replace)]
    [Arguments(OpenCodeServerVersionPolicy.Error)]
    public async Task ResolveAsync_Should_Ensure_The_Snapshot_With_The_Callers_Request(OpenCodeServerVersionPolicy policy)
    {
        Action<OpenCodeServerEnsureStartContext> onStart = _ => throw new InvalidOperationException("door substitution never starts");
        var service = new OpenCodeServerEnsureOptions { ExpectedVersion = "2.2.0", VersionPolicy = policy, OnStart = onStart };

        await using var connection = await ResolveAsync(new() { Service = service });

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Service);
        await Assert.That(connection.ServiceOperations).IsNotNull();
        await AssertCallsAsync("EnsureLocatedAsync");
        var request = (EnsureRequest)_doors.ReceivedCalls().Single().GetArguments()[0]!;
        await Assert.That(request.OnStart).IsSameReferenceAs(onStart);
        await Assert.That(request.Policy).IsEqualTo(policy);
        await Assert.That(request.Selection.ExpectedVersion).IsEqualTo("2.2.0");
        await Assert.That(request.LoopVersion).IsEqualTo(policy == OpenCodeServerVersionPolicy.Ignore ? null : "2.2.0");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveAsync_Should_Report_The_Registered_Version_In_Service_Mode(bool autostart)
    {
        _doors.DiscoverLocatedAsync(Arg.Any<ServicePaths>(), expectedVersion: null, Arg.Any<CancellationToken>()).Returns(_ => Shared("2.2.7"));
        await using var connection = await ResolveAsync(new() { Autostart = autostart });

        await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Service);
        await Assert.That(connection.ServerVersion).IsEqualTo("2.2.7");
        await Assert.That(connection.Server!.RegisteredVersion).IsEqualTo("2.2.7");
    }

    [Test]
    public async Task ResolveAsync_Should_Not_Default_A_Service_Version_To_The_Pin()
    {
        _doors.EnsureLocatedAsync(Arg.Any<EnsureRequest>(), Arg.Any<ServicePaths>(), Arg.Any<ServiceConfigEnvironment>(), Arg.Any<CancellationToken>())
            .Returns(_ => Shared(null));
        await using var connection = await ResolveAsync(null);

        var request = (EnsureRequest)_doors.ReceivedCalls().Single().GetArguments()[0]!;
        await Assert.That(request.LoopVersion).IsNull();
        await Assert.That(request.Selection.ExpectedVersion).IsNull();
        await Assert.That(connection.ServerVersion).IsNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ResolveAsync_Should_Discover_Any_Version_When_Autostart_Is_False(bool hit)
    {
        _doors.DiscoverLocatedAsync(Arg.Any<ServicePaths>(), expectedVersion: null, Arg.Any<CancellationToken>())
            .Returns(_ => hit ? Shared("2.2.7") : null);
        var resolving = ResolveAsync(new()
        {
            Autostart = false,
            Service = new OpenCodeServerEnsureOptions { ExpectedVersion = "2.2.0", VersionPolicy = OpenCodeServerVersionPolicy.Error },
        });
        await Assert.That(resolving).ThrowsNothing();
        await using var connection = await resolving;

        await Assert.That(connection.Mode).IsEqualTo(hit ? OpenCodeServerConnectionMode.Service : OpenCodeServerConnectionMode.Standalone);
        await Assert.That(connection.ServerVersion).IsEqualTo(hit ? "2.2.7" : null);
        await Assert.That(connection.ServiceOperations is null).IsEqualTo(!hit);
        if (hit)
        {
            await AssertCallsAsync("DiscoverLocatedAsync");
        }
        else
        {
            await AssertCallsAsync("DiscoverLocatedAsync", "StartAsync");
        }
        await Assert.That(_doors.ReceivedCalls().First().GetArguments()[1]).IsNull();
    }

    [Test]
    public async Task ResolveAsync_Should_Copy_The_Service_Options_At_The_Call()
    {
        var command = new[] { "original", "serve" };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "1" };
        Action<OpenCodeServerEnsureStartContext> onStart = _ => throw new InvalidOperationException("unused");
        var service = new OpenCodeServerEnsureOptions { Channel = "dev", Command = command, Environment = environment, OnStart = onStart };
        await using var connection = await ResolveAsync(new() { Service = service });
        var initial = _doors.ReceivedCalls().Single().GetArguments();
        service.Channel = "other";
        command[0] = "changed";
        environment["A"] = "2";
        service.OnStart = null;

        await using var reconnected = await connection.ServiceOperations!.ReconnectAsync();

        var next = _doors.ReceivedCalls().Last().GetArguments();
        await Assert.That(((EnsureRequest)initial[0]!).OnStart).IsSameReferenceAs(onStart);
        var request = (EnsureRequest)next[0]!;
        await Assert.That(request.OnStart).IsNull();
        await Assert.That(request.Command).IsEquivalentTo(["original", "serve"], CollectionOrdering.Matching);
        await Assert.That(request.Environment).IsEquivalentTo([new KeyValuePair<string, string>("A", "1")], CollectionOrdering.Matching);
        await Assert.That(next[1]).IsSameReferenceAs(initial[1]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolveAsync_Should_Read_The_Config_And_Donors_As_Upstream_Does(bool direct)
    {
        var service = direct
            ? new OpenCodeServerEnsureOptions { RegistrationFilePath = _mock.Path.Combine(_mock.Path.GetTempPath(), "direct.json") }
            : new OpenCodeServerEnsureOptions { Channel = "dev" };
        var paths = Paths(service);
        await using var connection = await ResolveAsync(new() { Service = service });

        string[] expected = direct ? [] : [paths.LegacyConfigFile!, paths.ConfigFile!, .. paths.LegacyRegistrationFiles,
            paths.LegacyConfigFile!, paths.ConfigFile!];
        await Assert.That(_reads).IsEquivalentTo(expected, CollectionOrdering.Matching);
        _reads.Clear();
        await using var reconnected = await connection.ServiceOperations!.ReconnectAsync();
        await using var restarted = await connection.ServiceOperations.RestartAsync();
        await Assert.That(_reads).IsEmpty();
    }

    [Test]
    public async Task ReconnectAsync_Should_Use_The_Captured_Absent_Overlay()
    {
        var service = new OpenCodeServerEnsureOptions { Channel = "dev" };
        var paths = Paths(service);
        await using var connection = await ResolveAsync(new() { Service = service });
        var captured = (ServiceConfigEnvironment)_doors.ReceivedCalls().Single().GetArguments()[2]!;
        Seed(paths.ConfigFile!, new FixtureLoader().LoadJson("BackgroundService.service-config-env-state-home.json"));
        _reads.Clear();

        await using var reconnected = await connection.ServiceOperations!.ReconnectAsync();

        await Assert.That(_doors.ReceivedCalls().Last().GetArguments()[2]).IsSameReferenceAs(captured);
        await Assert.That(captured.Entries).IsNull();
        await Assert.That(_reads).IsEmpty();
    }

    [Test]
    [Arguments("standalone")]
    [Arguments("discovery-hit")]
    [Arguments("discovery-miss")]
    [Arguments("managed")]
    public async Task ResolveAsync_Should_Forward_The_Callers_Token_To_Every_Door_And_Read(string arm)
    {
        using var caller = new CancellationTokenSource();
        var options = TokenOptions(arm);
        // TokenOptions always supplies the selected channel.
        var paths = Paths(options.Service!);
        Seed(paths.ConfigFile!, new FixtureLoader().LoadJson("BackgroundService.service-config-env-state-home.json"));
        _doors.DiscoverLocatedAsync(Arg.Any<ServicePaths>(), expectedVersion: null, Arg.Any<CancellationToken>())
            .Returns(_ => arm == "discovery-hit" ? Shared("2.2.7") : null);

        await using var connection = await OpenCodeServer.ResolveWithSeamsAsync(options, _seams, caller.Token);

        if (options.Standalone || arm == "discovery-miss")
        {
            _ = _doors.Received(1).StartAsync(options.Launch, caller.Token);
            await Assert.That(connection.Mode).IsEqualTo(OpenCodeServerConnectionMode.Standalone);
        }
        if (!options.Standalone && !options.Autostart)
        {
            _ = _doors.Received(1).DiscoverLocatedAsync(Arg.Any<ServicePaths>(), expectedVersion: null, caller.Token);
        }
        if (!options.Standalone && options.Autostart)
        {
            _ = _doors.Received(1).EnsureLocatedAsync(
                Arg.Any<EnsureRequest>(), Arg.Any<ServicePaths>(), Arg.Any<ServiceConfigEnvironment>(), caller.Token);
        }
        if (options.Standalone)
        {
            await AssertCallsAsync("StartAsync");
            await Assert.That(_reads).IsEmpty();
        }
        else
        {
            string[] expectedDoors = arm switch
            {
                "discovery-hit" => ["DiscoverLocatedAsync"],
                "discovery-miss" => ["DiscoverLocatedAsync", "StartAsync"],
                _ => ["EnsureLocatedAsync"],
            };
            await AssertCallsAsync(expectedDoors);
            await Assert.That(_reads).IsEquivalentTo(
                [paths.LegacyConfigFile!, paths.ConfigFile!, .. paths.LegacyRegistrationFiles, paths.LegacyConfigFile!, paths.ConfigFile!],
                CollectionOrdering.Matching);
        }
        foreach (var read in _files.ReceivedCalls().Where(call =>
            call.GetMethodInfo().Name == nameof(IServiceFileSystem.TryReadAllBytesAsync)))
        {
            await Assert.That(read.GetArguments()[1]).IsEqualTo(caller.Token);
        }
    }

    [Test]
    [Arguments("standalone")]
    [Arguments("discovery-hit")]
    [Arguments("discovery-miss")]
    [Arguments("managed")]
    public async Task ResolveAsync_Should_Refuse_A_Cancelled_Call_Before_Any_Door_Or_Read(string arm)
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();

        var failure = await Assert.That(OpenCodeServer.ResolveWithSeamsAsync(TokenOptions(arm), _seams, caller.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(failure!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(_doors.ReceivedCalls()).IsEmpty();
        await Assert.That(_reads).IsEmpty();
        await Assert.That(_files.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
    }

    private static OpenCodeServerResolveOptions TokenOptions(string arm) => new()
    {
        Standalone = arm == "standalone",
        Autostart = arm is "standalone" or "managed",
        Launch = new OpenCodeServerOptions(),
        Service = new OpenCodeServerEnsureOptions { Channel = "dev" },
    };

    private Task<OpenCodeServerConnection> ResolveAsync(OpenCodeServerResolveOptions? options) =>
        OpenCodeServer.ResolveWithSeamsAsync(options, _seams, CancellationToken.None);

    private static HttpResponseMessage InfoReply(string version)
    {
        // The canonical info fixture is a valid object; only its version varies here.
        var document = JsonNode.Parse(WireBodyData.InfoOk)!;
        document["version"] = version;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(document.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private static OpenCodeServer Shared(string? version) => new(Endpoint, "pw", 42, version);

    private ServicePaths Paths(OpenCodeServerEnsureOptions service) =>
        new ServicePathResolver(_environment).Resolve(EnsureRequest.Snapshot(service).Selection);

    private void Seed(string path, string content) =>
        _ = _mock.Initialize().WithFile(path).Which(file => file.HasStringContent(content));

    private async Task AssertCallsAsync(params string[] expected) =>
        await Assert.That(_doors.ReceivedCalls().Select(call => call.GetMethodInfo().Name)).IsEquivalentTo(expected, CollectionOrdering.Matching);
}
