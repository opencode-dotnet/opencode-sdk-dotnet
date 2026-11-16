using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental.Support;

/// <summary>Owns deterministic loopback admission and scripted client-MCP observations.</summary>
internal sealed class ClientMcpScenario(Func<int>? reservePort = null) : IDisposable
{
    private HttpListener? _listener;
    private readonly Channel<TimeSpan> _retry = Channel.CreateUnbounded<TimeSpan>();
    private readonly Channel<ClientMcpDispatch> _opened = Channel.CreateUnbounded<ClientMcpDispatch>();
    private readonly List<ScriptedTerminalWebSocket> _sockets = [];
    private readonly List<TimeSpan> _delays = [];
    private readonly List<int> _attempts = [];
    private readonly List<ScriptedTerminalWebSocket> _owned = [];

    public ManualTimeProvider Clock { get; } = new();
    public IReadOnlyList<ScriptedTerminalWebSocket> Sockets => _sockets;
    public IReadOnlyList<TimeSpan> Delays => _delays;
    public IReadOnlyList<int> Attempts => _attempts;

    public ScriptedTerminalWebSocket Socket()
    {
        var socket = new ScriptedTerminalWebSocket();
        _owned.Add(socket);
        return socket;
    }

    public static ClientMcpOptions Options(Func<System.Text.Json.JsonElement, ClientMcpToolContext, Task<ClientMcpToolResult>> handler) =>
        new() { Name = "app", Tools = [new ClientMcpTool { Name = "lookup", Handler = handler }] };

    public HttpListener Listener
    {
        get
        {
            if (_listener is null)
            {
                var ports = new List<string>();
                while (ports.Count < 5)
                {
                    _listener = new HttpListener();
                    try
                    {
                        var port = (reservePort ?? LoopbackPortReservation.Reserve)().ToString(CultureInfo.InvariantCulture);
                        ports.Add(port);
                        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                        _listener.Start();
                        return _listener;
                    }
                    // Measured with two registrations of one prefix on KAER: ERROR_ALREADY_EXISTS.
                    catch (HttpListenerException exception) when (OperatingSystem.IsWindows() && exception.ErrorCode == 183)
                    {
                        _listener.Close();
                        _listener = null;
                    }
                }

                throw new InvalidOperationException($"No client MCP listener port could be registered: {string.Join(", ", ports)}.");
            }

            return _listener;
        }
    }

    public ClientMcpConnection Connect(IReadOnlyList<ScriptedTerminalWebSocket> sockets, ClientMcpOptions? options = null,
        Func<ITerminalWebSocket>? create = null, Func<ITerminalWebSocket, CancellationToken, Task<ITerminalWebSocket>>? upgrade = null)
    {
        var index = 0;
        return new ClientMcpConnection(options ?? new ClientMcpOptions { Name = "app", Tools = [] }, Clock, create ?? (() =>
        {
            var socket = sockets[index++];
            _sockets.Add(socket);
            return socket;
        }), upgrade ?? (static (socket, _) => Task.FromResult(socket)), (attempt, delay) =>
        {
            _attempts.Add(attempt);
            _delays.Add(delay);
            _ = _retry.Writer.TryWrite(delay);
        }, dispatch => _ = _opened.Writer.TryWrite(dispatch));
    }

    public Task<TimeSpan> RetryAsync(ClientMcpConnection connection) => NextAsync(_retry, connection);

    public static async Task<string> ExchangeAsync(WebSocket peer, string method)
    {
        var request = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = 7, ["method"] = method }.ToJsonString());
        await peer.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var bytes = new List<byte>();
        WebSocketReceiveResult frame;
        do
        {
            var buffer = new byte[1024];
            frame = await peer.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            bytes.AddRange(buffer.Take(frame.Count));
        } while (!frame.EndOfMessage);

        return Encoding.UTF8.GetString([.. bytes]);
    }

    public async Task<ClientMcpDispatch> OpenedAsync(ClientMcpConnection connection) =>
        await NextAsync(_opened, connection) ?? throw new InvalidOperationException("Connection completed before dispatch admission.");

    public static ClientMcpMessage Message(string method)
    {
        var root = new JsonObject { ["id"] = 7, ["method"] = method };
        if (method is "tools/call")
        {
            root["params"] = new JsonObject { ["name"] = "lookup" };
        }

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        return new ClientMcpFrameDecoder().Decode(WebSocketMessageType.Text, bytes, bytes.Length);
    }

    private static async Task<T?> NextAsync<T>(Channel<T> channel, ClientMcpConnection connection)
    {
        using var end = new CancellationTokenSource();
        var stopped = connection.Completion.ContinueWith(_ => channel.Writer.TryWrite(default!),
            end.Token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await channel.Reader.ReadAsync();
        }
        finally
        {
            await end.CancelOnWorkerAsync();
            _ = await stopped.ContinueWith(static completed => completed.Exception,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        _listener?.Close();
        foreach (var socket in _owned)
        {
            socket.Dispose();
        }
    }
}
