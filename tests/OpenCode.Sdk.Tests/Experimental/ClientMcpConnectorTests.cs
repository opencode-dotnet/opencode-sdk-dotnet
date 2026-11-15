using System.Net.WebSockets;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Experimental.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental;

public sealed class ClientMcpConnectorTests
{
    [Test]
    public async Task Create_Should_Dispose_A_Returned_Socket_When_Setup_Fails()
    {
        using var socket = new ClientWebSocket();
        var disposals = new List<ClientWebSocket>();
        var connector = new ClientMcpConnector(new ConnectionSnapshot("http://host.test", null, null, new ManualTimeProvider()),
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
        var connector = new ClientMcpConnector(new ConnectionSnapshot(listener.Prefixes.Single(), null, null, new ManualTimeProvider()),
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
