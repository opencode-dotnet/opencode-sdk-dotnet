using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Experimental.Support;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpConnectionTests
{
    private readonly JsonNode _wire = JsonNode.Parse(new FixtureLoader().LoadJson("Experimental.mcp-connection.json"))!;

    [Test]
    public async Task ConnectClientMcp_Should_Open_Through_The_Production_Admission_Seam()
    {
        using var scenario = new ClientMcpScenario();
        var listener = scenario.Listener;
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = new Uri(listener.Prefixes.Single()) });
        var created = new List<ClientWebSocket>();
        var options = new ClientMcpOptions
        {
            Name = "app",
            Tools = [],
            WebSocketFactory = () =>
            {
                var socket = new ClientWebSocket();
                created.Add(socket);
                return socket;
            },
        };
        var missingClient = await Assert.That(() => ExperimentalClientMcpExtensions.ConnectClientMcp(null!, options)).Throws<ArgumentNullException>();
        await Assert.That(missingClient!.ParamName).IsEqualTo("client");
        var missingOptions = await Assert.That(() => client.Experimental.ConnectClientMcp(null!)).Throws<ArgumentNullException>();
        await Assert.That(missingOptions!.ParamName).IsEqualTo("options");
        var mocking = await Assert.That(() => NSubstitute.Substitute.For<ExperimentalClient>().ConnectClientMcp(options)).Throws<InvalidOperationException>();
        await Assert.That(mocking!.Message).IsEqualTo("'ExperimentalClient.ConnectClientMcp' was invoked on an instance created through the protected mocking constructor; override the member under test.");
        var declared = ClientMcpScenario.Options(static (_, _) => Task.FromResult(Result("unused")));
        foreach (var invalid in new[] { options with { Name = null! }, options with { Tools = null! }, options with { Tools = [null!] },
            options with { Tools = [declared.Tools[0] with { Name = null! }] }, options with { Tools = [declared.Tools[0] with { Handler = null! }] } })
        {
            var refused = await Assert.That(() => client.Experimental.ConnectClientMcp(invalid)).Throws<ArgumentException>();
            await Assert.That(refused!.ParamName).IsEqualTo("options");
#if NET
            await Assert.That(refused.Message).IsEqualTo("Client MCP name, tools and handlers are required. (Parameter 'options')");
#else
            await Assert.That(refused.Message).IsEqualTo("Client MCP name, tools and handlers are required.\r\nParameter name: options");
#endif
        }

        await Assert.That(created).IsEmpty();
        using var invalidSchema = JsonDocument.Parse(_wire["reset"]!.ToJsonString());
        var badSchema = await Assert.That(async () =>
        {
            await using var admitted = client.Experimental.ConnectClientMcp(options with { Tools = [declared.Tools[0] with { InputSchema = invalidSchema.RootElement }] });
        }).Throws<ArgumentException>();
#if NET
        await Assert.That(badSchema!.Message).IsEqualTo("Client MCP input schemas must be objects. (Parameter 'options')");
#else
        await Assert.That(badSchema!.Message).IsEqualTo("Client MCP input schemas must be objects.\r\nParameter name: options");
#endif
        await using var connection = client.Experimental.ConnectClientMcp(options);
        await Assert.That(created.Count).IsEqualTo(1);
        var request = await listener.GetContextAsync();
        await Assert.That(request.Request.RawUrl).IsEqualTo("/api/experimental/client-mcp?name=app");
        using var peer = (await request.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
        await Assert.That(await ClientMcpScenario.ExchangeAsync(peer, "ping")).IsEqualTo(_wire["ping"]!.ToJsonString());
    }

    [Test]
    public async Task ConnectClientMcp_Should_Capture_A_Manual_Schema_Before_Its_Document_Ends()
    {
        using var scenario = new ClientMcpScenario();
        var listener = scenario.Listener;
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = new Uri(listener.Prefixes.Single()) });
        using var document = JsonDocument.Parse(_wire["schema"]!.ToJsonString());
        await using var connection = client.Experimental.ConnectClientMcp(new ClientMcpOptions
        {
            Name = "app",
            Tools = [new ClientMcpTool { Name = "lookup", InputSchema = document.RootElement, Handler = static (_, _) => Task.FromResult(Result("unused")) }],
        });
        document.Dispose();
        var request = await listener.GetContextAsync();
        using var peer = (await request.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
        await Assert.That(await ClientMcpScenario.ExchangeAsync(peer, "tools/list")).IsEqualTo(_wire["manual"]!.ToJsonString());
    }

    [Test]
    public async Task ConnectClientMcp_Should_Not_Mint_Or_Refresh_A_Ticket()
    {
        using var scenario = new ClientMcpScenario();
        var listener = scenario.Listener;
        using var recorder = new RecordingHttpHandler();
        using var http = new HttpClient(recorder);
        using var pipeline = new Pipeline(http, ownsHttpClient: false,
            new OpenCodeClientOptions { Endpoint = new Uri(listener.Prefixes.Single()), Password = "synthetic" }, time: scenario.Clock);
        await using var connection = new ExperimentalClient(pipeline).ConnectClientMcp(new ClientMcpOptions { Name = "app", Tools = [] });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = await listener.GetContextAsync();
            await Assert.That(request.Request.HttpMethod).IsEqualTo("GET");
            await Assert.That(request.Request.RawUrl).IsEqualTo("/api/experimental/client-mcp?name=app");
            await Assert.That(request.Request.Headers["Authorization"]).IsEqualTo("Basic b3BlbmNvZGU6c3ludGhldGlj");
            using var peer = (await request.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
            await Assert.That(await ClientMcpScenario.ExchangeAsync(peer, "ping")).IsEqualTo(_wire["ping"]!.ToJsonString());
            if (attempt is 0)
            {
                await peer.CloseAsync(WebSocketCloseStatus.InternalServerError, "attach failed", CancellationToken.None);
                await scenario.Clock.WaitForArmedAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
                scenario.Clock.Advance(TimeSpan.FromMilliseconds(500));
            }
        }

        await connection.DisposeAsync();
        await Assert.That(recorder.Requests).IsEmpty();
    }

    [Test]
    public async Task ConnectClientMcp_Should_Propagate_Initial_Factory_Failure_Synchronously()
    {
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = new Uri("http://host.test") });
        var injected = new InvalidOperationException("factory-failure");
        var failure = await Assert.That(() => client.Experimental.ConnectClientMcp(new ClientMcpOptions
        {
            Name = "app",
            Tools = [],
            WebSocketFactory = () => throw injected,
        })).Throws<InvalidOperationException>();
        await Assert.That(failure).IsSameReferenceAs(injected);
    }

    [Test]
    public async Task Connect_Should_Record_The_Exact_Capped_Retry_Schedule()
    {
        using var scenario = new ClientMcpScenario();
        using var last = new ScriptedTerminalWebSocket();
        ScriptedTerminalWebSocket[] sockets = [.. Enumerable.Range(0, 7).Select(static _ => new ScriptedTerminalWebSocket().Closing(WebSocketCloseStatus.NormalClosure)), last];
        await using var connection = scenario.Connect(sockets);
        var expected = new[] { 500, 1000, 2000, 4000, 8000, 10000, 10000 };
        for (var index = 0; index < expected.Length; index++)
        {
            await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(expected[index]));
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(expected[index] - 1));
            await Assert.That(scenario.Sockets.Count).IsEqualTo(index + 1);
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await Assert.That(scenario.Sockets[index + 1]).IsSameReferenceAs(sockets[index + 1]);
        }

        await Assert.That(scenario.Delays).IsEquivalentTo(expected.Select(static value => TimeSpan.FromMilliseconds(value)), CollectionOrdering.Matching);
        await Assert.That(scenario.Attempts).IsEquivalentTo(Enumerable.Range(2, 7), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Connect_Should_Reset_Backoff_Only_On_Initialized()
    {
        using var scenario = new ClientMcpScenario();
        var third = scenario.Socket().Pausing().Closing(WebSocketCloseStatus.NormalClosure);
        var fourth = scenario.Socket().Pausing().Closing(WebSocketCloseStatus.NormalClosure);
        await using var connection = scenario.Connect([scenario.Socket().Closing(WebSocketCloseStatus.NormalClosure),
            scenario.Socket().Closing(WebSocketCloseStatus.NormalClosure), third, fourth, scenario.Socket()]);
        foreach (var delay in new[] { 500, 1000 })
        {
            _ = await scenario.OpenedAsync(connection);
            await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(delay));
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(delay));
        }

        var dispatch = await scenario.OpenedAsync(connection);
        await dispatch.DispatchAsync(ClientMcpScenario.Message("initialize"));
        await dispatch.DispatchAsync(ClientMcpScenario.Message("tools/list"));
        await dispatch.ObserveResponsesAsync();
        await Assert.That(third.SentText).IsEquivalentTo(_wire["reset"]!.AsArray().Select(static node => node!.ToJsonString()), CollectionOrdering.Matching);
        scenario.Clock.Advance(TimeSpan.FromSeconds(60));
        third.ReleaseReceives();
        await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(2000));
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(2000));
        await (await scenario.OpenedAsync(connection)).DispatchAsync(ClientMcpScenario.Message("notifications/initialized"));
        fourth.ReleaseReceives();
        await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    [Arguments(1000, "normal")]
    [Arguments(1011, "attach failed")]
    [Arguments(1012, "location shut down")]
    public async Task Connect_Should_Retry_Actual_Close_Outcomes(int code, string reason)
    {
        using var scenario = new ClientMcpScenario();
        var socket = scenario.Socket().Closing((WebSocketCloseStatus)code, reason);
        await using var connection = scenario.Connect([socket, scenario.Socket()]);
        await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(socket.CloseStatus).IsEqualTo((WebSocketCloseStatus)code);
        await Assert.That(socket.CloseStatusDescription).IsEqualTo(reason);
        await Assert.That(new ClientMcpClosePolicy("app").Map(socket.CloseStatus, socket.CloseStatusDescription).Message)
            .IsEqualTo($"The opencode client MCP 'app' WebSocket closed with code {code.ToString(System.Globalization.CultureInfo.InvariantCulture)} ({reason}).");
        scenario.Clock.Advance(TimeSpan.FromMilliseconds(500));
        await Assert.That(scenario.Attempts).IsEquivalentTo([2], CollectionOrdering.Matching);
        await Assert.That(scenario.Sockets.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Connect_Should_Retry_An_Injected_Upgrade_Failure()
    {
        using var scenario = new ClientMcpScenario();
        var socket = scenario.Socket();
        var failure = new ClientMcpUpgradeFailurePolicy().Map(new WebSocketException(), 403, "app");
        await using var connection = scenario.Connect([socket], upgrade: (_, _) => Task.FromException<ITerminalWebSocket>(failure));
        await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(500));
        await Assert.That(failure.Message).IsEqualTo("The opencode client MCP 'app' WebSocket upgrade failed with HTTP 403.");
        await Assert.That(scenario.Attempts).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Connect_Should_Stop_And_Observe_A_Later_Factory_Failure()
    {
        using var scenario = new ClientMcpScenario();
        var socket = scenario.Socket().Closing(WebSocketCloseStatus.NormalClosure);
        var injected = new InvalidOperationException("later-factory");
        var calls = 0;
        await using var connection = scenario.Connect([], create: () => ++calls is 1 ? socket : throw injected);
        await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(500));
        var thrown = await Assert.That(() => scenario.Clock.Advance(TimeSpan.FromMilliseconds(500))).Throws<InvalidOperationException>();
        await Assert.That(thrown).IsSameReferenceAs(injected);
        await Assert.That(connection.Completion.Exception!.InnerException).IsSameReferenceAs(injected);
        await Assert.That(scenario.Delays).IsEquivalentTo([TimeSpan.FromMilliseconds(500)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DisposeAsync_Should_Stop_Retry_And_Close_Only_Its_Socket_Once()
    {
        using var scenario = new ClientMcpScenario();
        var one = scenario.Socket();
        var two = scenario.Socket();
        await using var first = scenario.Connect([one]);
        _ = await scenario.OpenedAsync(first);
        await using var second = scenario.Connect([two]);
        var serving = await scenario.OpenedAsync(second);
        var left = first.DisposeAsync();
        var right = first.DisposeAsync();
        await Task.WhenAll(left.AsTask(), right.AsTask());
        await Assert.That(one.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(one.DisposeCalls).IsEqualTo(1);
        await Assert.That(two.DisposeCalls).IsEqualTo(0);
        await serving.DispatchAsync(ClientMcpScenario.Message("ping"));
        await serving.ObserveResponsesAsync();
        await Assert.That(two.SentText).IsEquivalentTo([_wire["ping"]!.ToJsonString()], CollectionOrdering.Matching);
        await second.DisposeAsync();
        scenario.Clock.Advance(TimeSpan.FromHours(1));
        await Assert.That(two.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(two.DisposeCalls).IsEqualTo(1);
        await Assert.That(scenario.Sockets.Count).IsEqualTo(2);
        await Assert.That(scenario.Sockets[0]).IsSameReferenceAs(one);
        await Assert.That(scenario.Sockets[1]).IsSameReferenceAs(two);
    }

    [Test]
    public async Task DisposeAsync_Should_Not_Cancel_Or_Join_Application_Work()
    {
        using var scenario = new ClientMcpScenario();
        TaskCompletionSource<ClientMcpToolResult>? held = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = scenario.Socket();
        await using var connection = scenario.Connect([socket], ClientMcpScenario.Options((_, _) =>
        {
            var work = new TaskCompletionSource<ClientMcpToolResult>();
            held = work;
            _ = entered.TrySetResult(true);
            return work.Task;
        }));
        var dispatch = await scenario.OpenedAsync(connection);
        try
        {
            await dispatch.DispatchAsync(ClientMcpScenario.Message("tools/call"));
            await entered.Task.WaitAsync(HangGuard.Bound);
            await Assert.That(held).IsNotNull();
            var observed = dispatch.ObserveResponsesAsync();
            await Assert.That(observed.IsCompleted).IsFalse();
            await connection.DisposeAsync();
            await Assert.That(held!.Task.IsCompleted).IsFalse();
            held.SetResult(Result("nonce"));
            await observed;
            await Assert.That(socket.SentText).IsEmpty();
        }
        finally
        {
            _ = held?.TrySetResult(Result("nonce"));
            await dispatch.ObserveResponsesAsync();
        }
    }

    [Test]
    public async Task Connect_Should_Fence_A_Settled_Old_Response_From_The_New_Socket()
    {
        using var scenario = new ClientMcpScenario();
        var held = new TaskCompletionSource<ClientMcpToolResult>();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = scenario.Socket().Pausing().Closing(WebSocketCloseStatus.NormalClosure);
        var two = scenario.Socket();
        var calls = 0;
        await using var connection = scenario.Connect([one, two], ClientMcpScenario.Options((_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call is 1)
            {
                _ = entered.TrySetResult(true);
            }
            return call is 1 ? held.Task : Task.FromResult(Result("new"));
        }));
        var old = await scenario.OpenedAsync(connection);
        try
        {
            await old.DispatchAsync(ClientMcpScenario.Message("tools/call"));
            await entered.Task.WaitAsync(HangGuard.Bound);
            one.ReleaseReceives();
            await Assert.That(await scenario.RetryAsync(connection)).IsEqualTo(TimeSpan.FromMilliseconds(500));
            scenario.Clock.Advance(TimeSpan.FromMilliseconds(500));
            var current = await scenario.OpenedAsync(connection);
            await current.DispatchAsync(ClientMcpScenario.Message("tools/call"));
            await current.ObserveResponsesAsync();
            held.SetResult(Result("old"));
            await old.ObserveResponsesAsync();
            await Assert.That(two.SentText).IsEquivalentTo([_wire["new"]!.ToJsonString()], CollectionOrdering.Matching);
            await Assert.That(one.SentText).IsEmpty();
            await Assert.That(calls).IsEqualTo(2);
        }
        finally
        {
            _ = held.TrySetResult(Result("old"));
            await old.ObserveResponsesAsync();
        }
    }

    [Test]
    public async Task Connect_Should_Keep_Reply_Sends_Unbounded_Until_Socket_End()
    {
        using var scenario = new ClientMcpScenario();
        var socket = scenario.Socket().GatingSends();
        await using var connection = scenario.Connect([socket]);
        var dispatch = await scenario.OpenedAsync(connection);
        await dispatch.DispatchAsync(ClientMcpScenario.Message("ping"));
        try
        {
            await socket.SendEntered.WaitAsync(HangGuard.Bound);
            await Assert.That(socket.SentText).IsEquivalentTo([_wire["ping"]!.ToJsonString()], CollectionOrdering.Matching);
            var deadlines = scenario.Clock.ArmedIn(TimeSpan.FromSeconds(30));
            scenario.Clock.Advance(TimeSpan.FromSeconds(60));
            var completed = dispatch.ObserveResponsesAsync().IsCompleted;
            socket.ReleaseSends();
            await dispatch.ObserveResponsesAsync();
            await Assert.That(completed).IsFalse();
            await Assert.That(deadlines).IsEqualTo(0);
            await Assert.That(socket.MaxConcurrentSends).IsEqualTo(1);
        }
        finally
        {
            socket.ReleaseSends();
        }
    }

    private static ClientMcpToolResult Result(string text) => new() { Content = [ClientMcpContent.FromText(text)] };

}
