using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The repository-owned drive controller: a WebSocket JSON-RPC client for the
/// simulation backend. One JSON-RPC message per WebSocket text message
/// (control-server.ts:149-169); responses correlate by numeric id; llm.request,
/// tool.invocation, and tool.cancel arrive as id-less notifications, each on its own FIFO so
/// one kind can never be mistaken for another. Every wait is bounded — an unattached or wedged
/// controller must fail the suite fast, never hang it.
/// </summary>
internal sealed class DriveController : IAsyncDisposable
{
    /// <summary>
    /// The bound on sending one request and, separately, on its answer. A caller that wraps a round
    /// trip in its own deadline keeps that deadline above twice this value, so this bound's named
    /// failure is the one a stuck request reports.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The bound on the graceful close frame, and on the receive loop's unwind after it.</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(5);

    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonDocument>> _responses = new();
    private readonly DriveNotificationQueue<DriveInvocation> _requests = new("llm.request");
    private readonly DriveNotificationQueue<DriveToolInvocation> _toolInvocations = new("tool.invocation");
    private readonly DriveNotificationQueue<DriveToolCancellation> _toolCancellations = new("tool.cancel");
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _receiveLoop;
    private long _nextId;
    private int _disposed;

    /// <summary>
    /// What became of the receive loop, recorded the moment it stops reading. Without it a socket
    /// that died mid-run reports only as "the backend did not answer", which sends a reader to the
    /// backend when the connection is what broke; the bounded waits below quote this instead.
    /// </summary>
    private string? _loopState;

    /// <summary>
    /// The receive loop is started here, from the instance constructor, rather than from the
    /// <see cref="ConnectAsync"/> factory below: a Task field assigned by directly calling an
    /// async method from this object's own constructor is a task this object owns, the same
    /// shape <c>LoopbackHttpServer</c>'s accept-loop field uses, so DisposeAsync awaiting it
    /// later does not read as awaiting a foreign task.
    /// </summary>
    private DriveController(ClientWebSocket socket)
    {
        _socket = socket;
        _receiveLoop = ReceiveLoopAsync();
    }

    public static async Task<DriveController> ConnectAsync(Uri backendEndpoint, TimeSpan timeout)
    {
        var socket = new ClientWebSocket();
        using var connectTimeout = new CancellationTokenSource(timeout);
        try
        {
            await socket.ConnectAsync(backendEndpoint, connectTimeout.Token);
            return new DriveController(socket);
        }
        catch (Exception exception)
        {
            socket.Dispose();
            if (exception is OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Could not reach the drive backend at {backendEndpoint} within {timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.",
                    exception);
            }

            throw;
        }
    }

    public async Task HandshakeAsync()
    {
        using var response = await RoundTripAsync(DriveProtocol.Handshake, "simulation.handshake");
        var role = response.RootElement.GetProperty("result").GetProperty("role").GetString();
        if (!string.Equals(role, "backend", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unexpected simulation handshake role '{role}'.");
        }
    }

    public async Task AttachAsync()
    {
        using var response = await RoundTripAsync(DriveProtocol.Attach, "llm.attach");
        if (!response.RootElement.GetProperty("result").GetProperty("attached").GetBoolean())
        {
            throw new InvalidOperationException("The drive backend refused the controller attachment.");
        }
    }

    public Task<DriveInvocation> WaitForRequestAsync(TimeSpan timeout) =>
        _requests.DequeueAsync(timeout, DescribeLoopState, _lifetime.Token);

    public Task<DriveToolInvocation> WaitForToolInvocationAsync(TimeSpan timeout) =>
        _toolInvocations.DequeueAsync(timeout, DescribeLoopState, _lifetime.Token);

    public Task<DriveToolCancellation> WaitForToolCancellationAsync(TimeSpan timeout) =>
        _toolCancellations.DequeueAsync(timeout, DescribeLoopState, _lifetime.Token);

    /// <summary>Registers exactly these direct tools with the backend; an empty list resets the registration.</summary>
    public async Task AttachToolsAsync(IReadOnlyList<DriveToolRegistration> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        using var response = await RoundTripAsync(id => DriveProtocol.ToolAttach(id, tools), "tool.attach");
        if (!response.RootElement.GetProperty("result").GetProperty("attached").GetBoolean())
        {
            throw new InvalidOperationException("The drive backend refused the tool registration.");
        }
    }

    public async Task ChunkToolCallAsync(string invocationId, string callId, string name, JsonElement input)
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.ChunkToolCall(id, invocationId, callId, name, input), "llm.chunk");
        EnsureOk(response, "llm.chunk");
    }

    public async Task UpdateToolAsync(string toolId, int sequence, JsonElement update)
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.ToolUpdate(id, toolId, sequence, update), "tool.update");
        EnsureOk(response, "tool.update");
    }

    public async Task FinishToolAsync(string toolId, JsonElement structured, string text)
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.ToolFinish(id, toolId, structured, text), "tool.finish");
        EnsureOk(response, "tool.finish");
    }

    public async Task FailToolAsync(string toolId, string message)
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.ToolFail(id, toolId, message), "tool.fail");
        EnsureOk(response, "tool.fail");
    }

    public async Task ChunkTextAsync(string invocationId, params string[] deltas)
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.ChunkText(id, invocationId, deltas), "llm.chunk");
        EnsureOk(response, "llm.chunk");
    }

    public async Task FinishAsync(string invocationId, string reason = "stop")
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.Finish(id, invocationId, reason), "llm.finish");
        EnsureOk(response, "llm.finish");
    }

    public async Task DisconnectAsync(string invocationId)
    {
        using var response = await RoundTripAsync(
            id => DriveProtocol.Disconnect(id, invocationId), "llm.disconnect");
        EnsureOk(response, "llm.disconnect");
    }

    /// <summary>
    /// Reads the ids of the invocations the simulated provider holds pending. The fixture is
    /// shared by every test of the session, so a test asserts on its own invocation ids, or on
    /// what it added to a set it read first, never on a count that another test's leftover shares.
    /// </summary>
    public async Task<IReadOnlyList<string>> PendingInvocationIdsAsync()
    {
        using var response = await RoundTripAsync(DriveProtocol.Pending, "llm.pending");
        return [.. response.RootElement.GetProperty("result").GetProperty("invocations").EnumerateArray()
            .Select(static invocation => invocation.GetProperty("id").GetString()!)];
    }

    /// <summary>
    /// Closes the connection: a graceful close first, bounded so an unresponsive peer cannot
    /// stall the caller, then the socket's hard teardown. Disposal is idempotent — a test
    /// fixture teardown path can call this twice (an explicit call beside an
    /// <c>await using</c> scope, or an early failure-path dispose followed by a suite teardown
    /// dispose) — and a second pass must not re-cancel a fresh lifetime, re-close a disposed
    /// socket, or re-dispose the cancellation source, mirroring <c>PtySession</c>'s
    /// Interlocked-guarded DisposeAsync (src/OpenCode.Sdk/Ptys/PtySession.cs).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 1)
        {
            return;
        }

        await _lifetime.CancelOnWorkerAsync();
        _ = await TryCloseOutputAsync(_socket);

        // Read into a local before awaiting: awaiting the field expression directly reads to the
        // analyzer as awaiting a foreign task, the same defensive copy LoopbackHttpServer's
        // DisposeAsync takes for its own accept-loop field.
        var receiveLoop = _receiveLoop;

        // CancellationToken.None deliberately: the lifetime token above is already cancelled, so
        // tying this wait to it would resolve immediately and give the receive loop no window at
        // all to notice the cancellation and unwind. The bound is the only limit that belongs here.
        try
        {
            await receiveLoop.WaitAsync(TeardownTimeout, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            // The hard dispose below is what stops a loop that will not unwind, and a fixture
            // teardown must not wait on it any longer than this. Recorded rather than ignored: a
            // caller whose in-flight wait is racing this teardown reads the state below.
            RecordLoopState("still unwinding when disposal stopped waiting for it");
        }
        finally
        {
            // Unconditional: _disposed is already latched, so a wait that ended in anything but
            // the timeout above would otherwise leave the socket and the lifetime alive with no
            // second pass able to release them.
            _socket.Dispose();
            _lifetime.Dispose();
            _requests.Dispose();
            _toolInvocations.Dispose();
            _toolCancellations.Dispose();
        }
    }

    /// <summary>
    /// Closes the socket's output side, bounded so an unresponsive peer cannot stall a fixture
    /// teardown. Mirrors <c>PtySession.TryCloseAsync</c> (src/OpenCode.Sdk/Ptys/PtySession.cs),
    /// including its deliberately wide fault set: a socket refusing a close for a state reason
    /// must not escape a disposal.
    /// </summary>
    /// <returns>
    /// True when the close frame left; false when it did not. Discarded at the call site because
    /// the hard dispose that follows is unconditional, and a caller closing a connection has
    /// nothing left to do about a close frame that never went out.
    /// </returns>
    private static async Task<bool> TryCloseOutputAsync(ClientWebSocket socket)
    {
        using var closeTimeout = new CancellationTokenSource(TeardownTimeout);
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token);
            return true;
        }
        catch (WebSocketException)
        {
            // The socket may already be gone.
            return false;
        }
        catch (OperationCanceledException)
        {
            // The close timeout, or an already-cancelled lifetime.
            return false;
        }
        catch (ObjectDisposedException)
        {
            // A concurrent teardown path already disposed the socket.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Close before connect completes reports state, not failure.
            return false;
        }
    }

    /// <summary>
    /// Reads the model id out of an invocation's provider request body. Upstream sends
    /// <c>{id, url, body}</c> on both the llm.request notification and llm.pending
    /// (simulated-provider.ts:223,264,334), but that body is provider shaped, not protocol
    /// shaped: it is whatever the resolved provider package chose to send. A missing, non-object
    /// body or a missing, non-string <c>model</c> therefore yields null rather than throwing -
    /// the receive loop must still deliver an invocation a caller is parked waiting for, and a
    /// controller that faulted on one optional field would turn a readable assertion failure into
    /// a hang.
    /// </summary>
    private static string? ReadModel(JsonElement parameters) =>
        parameters.TryGetProperty("body", out var body) &&
        body.ValueKind is JsonValueKind.Object &&
        body.TryGetProperty("model", out var model) &&
        model.ValueKind is JsonValueKind.String
            ? model.GetString()
            : null;

    private static void EnsureOk(JsonDocument response, string method)
    {
        if (!response.RootElement.GetProperty("result").GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException($"The drive backend did not acknowledge {method}.");
        }
    }

    private async Task<JsonDocument> RoundTripAsync(Func<long, byte[]> compose, string method)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _responses[id] = waiter;
        var payload = compose(id);
        await _sendGate.WaitAsync(_lifetime.Token);
        try
        {
            using var sendTimeout = new CancellationTokenSource(RequestTimeout);
            await _socket.SendAsync(
                new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, sendTimeout.Token);
        }
        finally
        {
            _ = _sendGate.Release();
        }

        // The answer arriving is the condition; the timeout and the controller's lifetime are only
        // the two bounds on waiting for it. Both bounds mean the same thing to a caller - no
        // answer came - so both are reported as the one timeout the WhenAny race this replaces
        // also reported.
        JsonDocument response;
        try
        {
            response = await waiter.Task.WaitAsync(RequestTimeout, _lifetime.Token);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            _ = _responses.TryRemove(id, out _);
            throw new TimeoutException(
                $"The drive backend did not answer {method} (request {id.ToString(CultureInfo.InvariantCulture)}) within {RequestTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.{DescribeLoopState()}",
                exception);
        }

        if (response.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.GetProperty("message").GetString();
            response.Dispose();
            throw new InvalidOperationException($"Drive request {method} failed: {message}");
        }

        return response;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        RecordLoopState("the backend closed the connection");
                        return;
                    }

#if NET
                    await message.WriteAsync(buffer.AsMemory(0, received.Count), _lifetime.Token);
#else
                    await message.WriteAsync(buffer, 0, received.Count, _lifetime.Token);
#endif
                }
                while (!received.EndOfMessage);

                Dispatch(JsonDocument.Parse(message.ToArray()));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal ended the loop. Recorded rather than rethrown: the loop's task is awaited
            // by that same disposal, which must not be handed back the cancellation it asked for.
            RecordLoopState("the controller was disposed");
        }
        catch (WebSocketException exception)
        {
            // Teardown races are the disposal's business, not the loop's - but a socket that
            // faulted mid-run is exactly what a later timeout needs to be able to name.
            RecordLoopState($"the WebSocket faulted ({exception.WebSocketErrorCode})");
        }
        catch (ObjectDisposedException)
        {
            // Disposal already tore the socket down; the loop simply stops.
            RecordLoopState("the socket was already disposed");
        }
        catch (Exception exception)
        {
            // Deliberately total. The concrete shapes this exists for are JsonException from
            // JsonDocument.Parse on a malformed frame, and KeyNotFoundException or
            // InvalidOperationException from Dispatch's GetProperty walk over a notification whose
            // shape drifted after a spec refresh - but a fault of any type here is worse than the
            // three named ones deserve: it leaves _loopState unset, so every bounded wait reports
            // "the backend did not answer" and sends a reader to the backend when the connection is
            // what broke. Recorded with its type and message instead, which is what those waits
            // quote. CA1031 is off repo-wide (.editorconfig §6.1) for exactly this shape.
            RecordLoopState($"the loop faulted ({exception.GetType().Name}: {exception.Message})");
        }
    }

    /// <summary>Records what became of the receive loop, for the bounded waits to quote.</summary>
    private void RecordLoopState(string reason) => Volatile.Write(ref _loopState, reason);

    /// <summary>
    /// Names what became of the receive loop, as a sentence a timeout message can append. A wait
    /// that expires while the loop is still reading normally says nothing extra.
    /// </summary>
    private string DescribeLoopState()
    {
        var reason = Volatile.Read(ref _loopState);
        return reason is null ? string.Empty : $" Receive loop: {reason}.";
    }

    private void Dispatch(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var id) &&
            id.ValueKind is JsonValueKind.Number &&
            _responses.TryRemove(id.GetInt64(), out var waiter))
        {
            if (!waiter.TrySetResult(document))
            {
                document.Dispose();
            }

            return;
        }

        using (document)
        {
            if (!root.TryGetProperty("method", out var method))
            {
                return;
            }

            var parameters = root.GetProperty("params");
            switch (method.GetString())
            {
                case "llm.request":
                    _requests.Enqueue(new DriveInvocation(
                        parameters.GetProperty("id").GetString()!,
                        parameters.GetProperty("url").GetString()!,
                        ReadModel(parameters),
                        parameters.TryGetProperty("body", out var body) ? body.Clone() : default));
                    break;
                case "tool.invocation":
                    var context = parameters.GetProperty("context");
                    _toolInvocations.Enqueue(new DriveToolInvocation(
                        parameters.GetProperty("id").GetString()!,
                        parameters.GetProperty("name").GetString()!,
                        parameters.GetProperty("input").Clone(),
                        context.GetProperty("sessionID").GetString()!,
                        context.GetProperty("agent").GetString()!,
                        context.GetProperty("messageID").GetString()!,
                        context.GetProperty("id").GetString()!));
                    break;
                case "tool.cancel":
                    _toolCancellations.Enqueue(new DriveToolCancellation(
                        parameters.GetProperty("id").GetString()!,
                        parameters.GetProperty("reason").GetString()!));
                    break;
                default:
                    // Other notifications are not this controller's business; they are neither
                    // queued nor faulted on.
                    break;
            }
        }
    }
}
