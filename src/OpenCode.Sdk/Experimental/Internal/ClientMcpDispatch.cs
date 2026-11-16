using System.Net.WebSockets;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>Admits overlapping responses and fences them to their original connection attempt.</summary>
internal sealed class ClientMcpDispatch(TerminalSocketCore<ClientMcpMessage> core, ClientMcpResponder responder, Action initialized)
{
    private readonly Lock _gate = new();
    private readonly List<Task> _responses = [];
    private bool _retired;

    public Task DispatchAsync(ClientMcpMessage message)
    {
        var method = ClientMcpResponder.Get(message.Root, "method");
        if (method.ValueKind is JsonValueKind.String && method.GetString() is "notifications/initialized")
        {
            initialized();
        }

        var id = ClientMcpResponder.Get(message.Root, "id");
        if (method.ValueKind is not JsonValueKind.String || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            if (_retired)
            {
                return Task.CompletedTask;
            }
        }

        var observed = SendResponseAsync(message).ContinueWith(static completed =>
        {
            _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (_gate)
        {
            _responses.Add(observed);
        }

        return Task.CompletedTask;
    }

    public Task ObserveResponsesAsync()
    {
        lock (_gate)
        {
            Task[] snapshot = [.. _responses];
            return Task.WhenAll(snapshot);
        }
    }

    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
        }
    }

    private async Task SendResponseAsync(ClientMcpMessage message)
    {
        var payload = await responder.RespondAsync(message).ConfigureAwait(false);
        Task send;
        lock (_gate)
        {
            if (_retired)
            {
                return;
            }

            send = core.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, CancellationToken.None);
        }

        await send.ConfigureAwait(false);
    }
}
