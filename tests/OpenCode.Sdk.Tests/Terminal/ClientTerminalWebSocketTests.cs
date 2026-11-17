using System.Net.Http.Headers;
using System.Net.WebSockets;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Terminal;

/// <summary>
/// The shipped adapter's upgrade contract. The adapter is family-neutral, so the wording of a
/// refused upgrade is not its to invent: what it owes is the guard that refuses to run without a
/// policy, and the routing that hands a real upgrade failure to the policy it was given.
/// </summary>
public sealed class ClientTerminalWebSocketTests
{
    private const string TerminalId = "pty_100";

    [Test]
    [Arguments(false, "Basic root")]
    [Arguments(true, "Bearer explicit")]
    public async Task ConnectAsync_Should_Apply_Root_And_Explicit_Handshake_Headers(bool overrideAuthorization, string expected)
    {
        using var scenario = new LoopbackUpgradeListener();
        var listener = scenario.Listener;
        using var raw = new ClientWebSocket();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Extra"] = "extra" };
        if (overrideAuthorization)
        {
            headers["Authorization"] = "Bearer explicit";
        }

        using var socket = new ClientTerminalWebSocket(raw, new AuthenticationHeaderValue("Basic", "root"), headers);
        var connect = socket.ConnectAsync(new UriBuilder(listener.Prefixes.Single()) { Scheme = "ws" }.Uri,
            "app", new ClientMcpUpgradeFailurePolicy(), CancellationToken.None);
        var context = listener.GetContextAsync();
        await Task.WhenAny(connect, context).Unwrap();
        var request = await context;
        using var peer = (await request.AcceptWebSocketAsync(subProtocol: null)).WebSocket;
        await connect;
        await Assert.That(request.Request.Headers["Authorization"]).IsEqualTo(expected);
        await Assert.That(request.Request.Headers["X-Extra"]).IsEqualTo("extra");
    }

    /// <summary>
    /// Loopback port 1 is the address nothing in this repository ever binds, so the upgrade is
    /// refused by the kernel on the first connect - no DNS, no listener, no wall-clock wait.
    /// </summary>
    private static readonly Uri Unreachable = new("ws://127.0.0.1:1/api/pty/pty_100/connect");

#if NET
    [Test]
    public async Task HttpHandler_Should_Release_The_Upgrade_Invoker_Without_Disposing_The_Handler()
    {
        using var handler = new RecordingHttpHandler();
        using var socket = new ClientTerminalWebSocket(authorization: null, handler);
        var invoker = (HttpMessageInvoker)typeof(ClientTerminalWebSocket)
            .GetField("_invoker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(socket)!;
        socket.Dispose();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://caller-handler.invalid");

        _ = await Assert.That(async () => _ = await invoker.SendAsync(request, CancellationToken.None)).Throws<ObjectDisposedException>();
        await Assert.That(handler.IsDisposed).IsFalse();
    }
#endif

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HttpHandler_Should_Carry_Terminal_Upgrades_Only_On_Modern_Targets(bool persistent)
    {
        await using var fallback = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse
        {
            StatusCode = System.Net.HttpStatusCode.NotFound,
        });
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        using var client = new OpenCodeClient(new OpenCodeClientOptions
        {
            Endpoint = fallback.Endpoint,
            Password = "secret",
            HttpHandler = handler,
        });
        var failure = await Assert.That(async () =>
        {
            if (persistent)
            {
                await using var session = await client.PersistentPtys.GetPersistentPtyClient(TerminalId).ConnectAsync();
            }
            else
            {
                await using var session = await client.Ptys.GetPtyClient(TerminalId).ConnectAsync();
            }
        }).Throws<OpenCodeTransportException>();

#if NET
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
        var upgrade = handler.Requests.Single();
        await Assert.That(upgrade.RequestUri!.AbsolutePath).IsEqualTo(persistent
            ? "/api/experimental/persistent-pty/pty_100/connect" : "/api/pty/pty_100/connect");
        await Assert.That(upgrade.Authorization).IsEqualTo("Basic b3BlbmNvZGU6c2VjcmV0");
        await Assert.That(failure!.Message).Contains("403");
        await Assert.That(fallback.Requests).IsEmpty();
#else
        await Assert.That(handler.Requests).IsEmpty();
        await Assert.That(fallback.Requests.Single().Headers["Upgrade"]).IsEqualTo("websocket");
        await Assert.That(failure!.InnerException).IsTypeOf<WebSocketException>();
#endif
        client.Dispose();
        await Assert.That(handler.IsDisposed).IsFalse();
    }

    [Test]
    public async Task ConnectAsync_Should_Refuse_A_Missing_Upgrade_Failure_Policy()
    {
        using var socket = new ClientTerminalWebSocket(authorization: null, httpHandler: null);

        var failure = await Assert
            .That(async () => await socket.ConnectAsync(Unreachable, TerminalId, policy: null!, CancellationToken.None))
            .Throws<ArgumentNullException>();

        await Assert.That(failure!.ParamName).IsEqualTo("policy");
    }

    [Test]
    public async Task ConnectAsync_Should_Route_A_Refused_Upgrade_Through_The_Injected_Policy()
    {
        using var socket = new ClientTerminalWebSocket(authorization: null, httpHandler: null);
        var policy = new RecordingUpgradeFailurePolicy();

        var failure = await Assert
            .That(async () => await socket.ConnectAsync(Unreachable, TerminalId, policy, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        // The failure the caller sees is the policy's own, carrying the terminal the adapter was
        // told about: the family owns the wording, the adapter owns only the upgrade.
        await Assert.That(failure!.Message).IsEqualTo(RecordingUpgradeFailurePolicy.Message);
        await Assert.That(policy.Calls).IsEqualTo(1);
        await Assert.That(policy.MappedTerminalId).IsEqualTo(TerminalId);
    }

    /// <summary>Records what the adapter handed the upgrade-failure seam.</summary>
    private sealed class RecordingUpgradeFailurePolicy : ITerminalUpgradeFailurePolicy
    {
        public const string Message = "The scripted upgrade-failure policy named this refusal.";

        public int Calls { get; private set; }

        public string? MappedTerminalId { get; private set; }

        public OpenCodeTransportException Map(WebSocketException exception, int? status, string terminalId)
        {
            Calls++;
            MappedTerminalId = terminalId;
            return new OpenCodeTransportException(Message, exception);
        }
    }
}
