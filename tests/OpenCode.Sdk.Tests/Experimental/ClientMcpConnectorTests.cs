using System.Net.WebSockets;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Experimental.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpConnectorTests
{
#if NET
    [Test]
    public async Task UpgradeAsync_Should_Use_The_Client_HttpHandler_For_A_Default_Socket()
    {
        await using var fallback = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = System.Net.HttpStatusCode.NotFound });
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = fallback.Endpoint, HttpHandler = handler });
        var connector = new ClientMcpConnector(client.Experimental.PipelineOrNull!.Connection, new ClientMcpOptions { Name = "app", Tools = [] });
        using var socket = connector.Create();
        _ = await Assert.That(async () => _ = await connector.UpgradeAsync(socket, CancellationToken.None)).Throws<OpenCodeTransportException>();

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        var upgrade = handler.Requests.Single();
        await Assert.That(upgrade.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(upgrade.RequestUri!.AbsolutePath).IsEqualTo("/api/experimental/client-mcp");
        await Assert.That(upgrade.Headers["Upgrade"]).IsEqualTo("websocket");
        await Assert.That(fallback.Requests).IsEmpty();
        await Assert.That(handler.IsDisposed).IsFalse();
    }

    [Test]
    public async Task UpgradeAsync_Should_Leave_A_Factory_Sockets_HttpHandler_Untouched()
    {
        await using var fallback = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = System.Net.HttpStatusCode.NotFound });
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = fallback.Endpoint, HttpHandler = handler });
        var connector = new ClientMcpConnector(client.Experimental.PipelineOrNull!.Connection,
            new ClientMcpOptions { Name = "app", Tools = [], WebSocketFactory = static () => new ClientWebSocket() });
        using var socket = connector.Create();
        _ = await Assert.That(async () => _ = await connector.UpgradeAsync(socket, CancellationToken.None)).Throws<OpenCodeTransportException>();

        await Assert.That(handler.Requests).IsEmpty();
        var upgrade = fallback.Requests.Single();
        await Assert.That(upgrade.Method).IsEqualTo("GET");
        await Assert.That(upgrade.Path).IsEqualTo("/api/experimental/client-mcp?name=app");
        await Assert.That(upgrade.Headers["Upgrade"]).IsEqualTo("websocket");
    }
#endif

    [Test]
    public async Task Create_Should_Dispose_A_Returned_Socket_When_Setup_Fails()
    {
        using var socket = new ClientWebSocket();
        var disposals = new List<ClientWebSocket>();
        var connector = new ClientMcpConnector(new ConnectionSnapshot("http://host.test", null, null, null, new ManualTimeProvider()),
            new ClientMcpOptions
            {
                Name = "app",
                Tools = [],
                WebSocketFactory = () => socket,
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["invalid\r\nheader"] = "value" },
            }, disposals.Add);
        await Assert.That(() => connector.Create()).Throws<ArgumentException>();
        await Assert.That(disposals.Single()).IsSameReferenceAs(socket);
        await Assert.That(socket.State).IsEqualTo(WebSocketState.Closed);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UpgradeAsync_Should_Dispose_A_Refused_Socket_And_Transfer_A_Success(bool success)
    {
        using var scenario = new ClientMcpScenario();
        var listener = scenario.Listener;
        using var raw = new ClientWebSocket();
        var disposals = new List<ClientWebSocket>();
        var connector = new ClientMcpConnector(new ConnectionSnapshot(listener.Prefixes.Single(), null, null, null, new ManualTimeProvider()),
            new ClientMcpOptions { Name = "app", Tools = [], WebSocketFactory = () => raw }, disposals.Add);
        var adapter = connector.Create();
        var upgrade = connector.UpgradeAsync(adapter, CancellationToken.None);
        try
        {
            var request = await listener.GetContextAsync();
            using var peer = success ? (await request.AcceptWebSocketAsync(subProtocol: null)).WebSocket : null;
            if (!success)
            {
                request.Response.StatusCode = 403;
                request.Response.Close();
            }

            ITerminalWebSocket? transferred = null;
            OpenCodeTransportException? failure = null;
            try
            {
                transferred = await upgrade;
            }
            catch (OpenCodeTransportException exception)
            {
                failure = exception;
            }

            if (success)
            {
                await Assert.That(failure).IsNull();
                await Assert.That(disposals).IsEmpty();
                await Assert.That(transferred).IsSameReferenceAs(adapter);
                await using var core = new TerminalSocketCore<PtyFrame>(transferred!, PtyFrameDecoder.Instance, PtyClosePolicy.Instance,
                    typeof(ClientMcpOptions), new ManualTimeProvider());
                await core.DisposeAsync();
            }
            else
            {
                await Assert.That(failure).IsNotNull();
#if NET
                await Assert.That(failure!.Message).IsEqualTo("The opencode client MCP 'app' WebSocket upgrade failed with HTTP 403.");
#else
                await Assert.That(failure!.Message).IsEqualTo("The opencode client MCP 'app' WebSocket upgrade failed before the connection was established.");
#endif
            }

            await Assert.That(disposals.Single()).IsSameReferenceAs(raw);
#if NET
            await Assert.That(raw.State).IsEqualTo(WebSocketState.Closed);
#else
            await Assert.That(raw.State).IsEqualTo(success ? WebSocketState.Aborted : WebSocketState.Closed);
#endif
        }
        finally
        {
            adapter.Dispose();
            _ = await upgrade.ContinueWith(static completed => completed.Exception,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

    }
}
