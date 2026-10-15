using System.Reflection;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Hosting;

public sealed class OpenCodeServerContractTests
{
    private static readonly Uri DoorEndpoint = new("http://127.0.0.1:4096");

    private const string DoorPassword = "lease-credential";

    private static OpenCodeServer CreateStartedDoor() => new(DoorEndpoint, DoorPassword);

    private static OpenCodeServer CreateDiscoveredDoor() => new(DoorEndpoint, DoorPassword, processId: 48213);

    [Test]
    public async Task OwnsProcess_Should_Be_False_For_A_Bare_Mock()
    {
        await Assert.That(new MockableServer().OwnsProcess).IsFalse();
    }

    [Test]
    public async Task A_Discovered_Door_Should_Carry_The_Registration_Identity_And_Own_Nothing()
    {
        var server = CreateDiscoveredDoor();

        await Assert.That(server.OwnsProcess).IsFalse();
        await Assert.That(server.Endpoint).IsEqualTo(DoorEndpoint);
        await Assert.That(server.Password).IsEqualTo(DoorPassword);
        await Assert.That(server.ProcessId).IsEqualTo(48213);
    }

    [Test]
    public async Task DisposeAsync_Should_Not_Stop_A_Discovered_Server()
    {
        var server = CreateDiscoveredDoor();

        // Twice: idempotent, and nothing to end either time; the identity stays readable.
        await server.DisposeAsync();
        await server.DisposeAsync();

        await Assert.That(server.ProcessId).IsEqualTo(48213);
        await Assert.That(server.Endpoint).IsEqualTo(DoorEndpoint);
    }

    [Test]
    public async Task CreateClient_Should_Use_The_Discovered_Identity()
    {
        var server = CreateDiscoveredDoor();
        OpenCodeClientOptions? observed = null;

        using var client = server.CreateClient(options => observed = options);

        // The delegate sees identity unset, and the built client carries the registration's
        // endpoint and password: the discovered handle is a first-class door, not a mock.
        await Assert.That(observed!.Endpoint).IsNull();
        await Assert.That(observed.Password).IsNull();
        var pipeline = (Pipeline)typeof(OpenCodeClient)
            .GetField("_pipeline", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        await Assert.That(pipeline.Connection.EndpointBase).IsEqualTo("http://127.0.0.1:4096");
        await Assert.That(pipeline.Connection.Authorization).IsNotNull();
    }

    [Test]
    public async Task DiscoverAsync_Should_Refuse_Contradictory_Options_With_ArgumentException()
    {
        var options = new OpenCodeServerDiscoverOptions { Channel = "dev", InstalledVersion = "2.0.2", ExpectedVersion = "2.0.3" };

        var exception = await Assert
            .That(async () => _ = await OpenCodeServer.DiscoverAsync(options))
            .Throws<ArgumentException>();

        await Assert.That(exception!.ParamName).IsEqualTo("options");
    }

    [Test]
    public async Task CreateClient_Should_Hand_The_Delegate_Identity_Unset_Options()
    {
        var server = CreateStartedDoor();
        OpenCodeClientOptions? observed = null;

        using var client = server.CreateClient(options => observed = options);

        await Assert.That(observed!.Endpoint).IsNull();
        await Assert.That(observed.Username).IsEqualTo("opencode");
        await Assert.That(observed.Password).IsNull();
    }

    [Test]
    public async Task CreateClient_Should_Refuse_A_Configured_Endpoint()
    {
        var server = CreateStartedDoor();

        var refusal = await Assert.That(() => server.CreateClient(options =>
            options.Endpoint = new Uri("http://example.test"))).Throws<InvalidOperationException>();

        await Assert.That(refusal!.Message).Contains("identity");
    }

    [Test]
    public async Task CreateClient_Should_Refuse_A_Configured_Password()
    {
        var server = CreateStartedDoor();

        _ = await Assert.That(() => server.CreateClient(options => options.Password = "other"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CreateClient_Should_Refuse_A_Changed_Username()
    {
        var server = CreateStartedDoor();

        _ = await Assert.That(() => server.CreateClient(options => options.Username = "admin"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CreateClient_Should_Allow_Behavior_Members()
    {
        var server = CreateStartedDoor();

        using var client = server.CreateClient(options =>
            options.Location = new LocationSelector { Directory = "/tmp/workspace" });

        await Assert.That(client).IsNotNull();
    }

    [Test]
    public async Task CreateClient_Should_Carry_Every_Behavior_Member_Into_The_Built_Client()
    {
        var identityMembers = new HashSet<string>(StringComparer.Ordinal) { "Endpoint", "Username", "Password" };
        var behaviorMembers = typeof(OpenCodeClientOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && !identityMembers.Contains(property.Name))
            .Select(property => property.Name)
            .ToArray();

        // A newly added OpenCodeClientOptions behavior member lands here first: CreateClient's
        // hand-written copy-list cannot know about it on its own, so this assertion is what turns
        // that silence loud instead of quiet. Extend both the expected list and the propagation
        // check below in the same change that adds the member.
        await Assert.That(behaviorMembers).IsEquivalentTo(["Location", "NetworkTimeout"]);

        var server = CreateStartedDoor();
        var sentinelLocation = new LocationSelector { Directory = "/sentinel-workspace" };
        var sentinelWindow = TimeSpan.FromSeconds(42);

        using var client = server.CreateClient(options =>
        {
            options.Location = sentinelLocation;
            options.NetworkTimeout = sentinelWindow;
        });

        var pipeline = (Pipeline)typeof(OpenCodeClient)
            .GetField("_pipeline", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(client)!;
        await Assert.That(pipeline.Connection.Location).IsSameReferenceAs(sentinelLocation);
        var carriedWindow = (TimeSpan?)typeof(Pipeline)
            .GetField("_clientNetworkTimeout", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pipeline);
        await Assert.That(carriedWindow).IsEqualTo(sentinelWindow);
    }

    [Test]
    public async Task CreateClient_Should_Build_A_New_Client_Per_Call()
    {
        var server = CreateStartedDoor();

        using var first = server.CreateClient();
        using var second = server.CreateClient();

        await Assert.That(ReferenceEquals(first, second)).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_Should_Be_Idempotent_Without_A_Process()
    {
        var server = CreateStartedDoor();

        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Test]
    public async Task StartAsync_Should_Refuse_An_Empty_Command()
    {
        _ = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = [] })).Throws<ArgumentException>();
    }

    [Test]
    public async Task StartAsync_Should_Refuse_A_Blank_Command_Entry()
    {
        _ = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions { Command = ["bun", " "] })).Throws<ArgumentException>();
    }

    [Test]
    public async Task Endpoint_Should_Throw_The_Mock_Seam_Failure_On_A_Bare_Mock()
    {
        var mock = new MockableServer();

        var failure = await Assert.That(() => _ = mock.Endpoint).Throws<InvalidOperationException>();

        await Assert.That(failure!.Message).Contains("OpenCodeServer.Endpoint");
    }

    [Test]
    public async Task Exited_Should_Throw_The_Mock_Seam_Failure_On_A_Bare_Mock()
    {
        var mock = new MockableServer();

        var failure = await Assert.That(() => { _ = mock.Exited; }).Throws<InvalidOperationException>();

        await Assert.That(failure!.Message).Contains("OpenCodeServer.Exited");
    }

    /// <summary>
    /// A handle over a registered service owns no process, so its end is not observed: upstream's
    /// attach endpoint carries no exit either.
    /// </summary>
    [Test]
    public async Task Exited_Should_Throw_When_The_Handle_Owns_No_Process()
    {
        var shared = new OpenCodeServer(new Uri("http://127.0.0.1:4096"), "pw", processId: 4242);

        var failure = await Assert.That(() => { _ = shared.Exited; }).Throws<InvalidOperationException>();

        await Assert.That(shared.OwnsProcess).IsFalse();
        await Assert.That(failure!.Message).Contains("owns no process");
    }

    private sealed class MockableServer : OpenCodeServer
    {
    }
}
