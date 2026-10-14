using System.Net.Http.Headers;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="ITerminalWebSocket"/>: a thin adapter over <see cref="ClientWebSocket"/>.
/// This is the SDK's one transport divergence — the upgrade builds its own socket, so a
/// caller-supplied <see cref="HttpClient"/>, its proxy, and its handler chain do not apply. The
/// Basic credential rides the upgrade request's <c>Authorization</c> header, which is the
/// designed non-browser path: the API's authentication middleware skips credentials only for a
/// URL carrying a ticket, and the SDK never mints a ticket for its own connection (a single-use
/// credential in a URL that reaches logs is strictly worse than the header it already holds).
/// </summary>
internal sealed class ClientTerminalWebSocket : ITerminalWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public ClientTerminalWebSocket(AuthenticationHeaderValue? authorization)
    {
        if (authorization is not null)
        {
            _socket.Options.SetRequestHeader("Authorization", authorization.ToString());
        }

#if NET
        // The pre-upgrade refusals the server answers (404, 401, 403) are only distinguishable
        // from the response status, which the socket keeps solely when asked to.
        _socket.Options.CollectHttpResponseDetails = true;
#endif
    }

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    public string? CloseStatusDescription => _socket.CloseStatusDescription;

    /// <summary>Performs the upgrade, mapping a refusal onto the failure that names its cause.</summary>
    /// <param name="uri">The <c>ws</c> or <c>wss</c> address to upgrade.</param>
    /// <param name="terminalId">The terminal the upgrade addresses; it names the failure.</param>
    /// <param name="policy">The family's policy naming why an upgrade was refused.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the connection is established.</returns>
    public async Task ConnectAsync(
        Uri uri,
        string terminalId,
        ITerminalUpgradeFailurePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        try
        {
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException exception)
        {
            // A cancellation that surfaced as a socket fault is still the caller's cancellation.
            cancellationToken.ThrowIfCancellationRequested();
#if NET
            var status = (int)_socket.HttpStatusCode;
            throw policy.Map(exception, status is 0 ? null : status, terminalId);
#else
            // CollectHttpResponseDetails and ClientWebSocket.HttpStatusCode are .NET 7 and later,
            // so neither downlevel leg can surface a refused upgrade's response status; the
            // failure names the connect context instead of guessing one.
            throw policy.Map(exception, status: null, terminalId);
#endif
        }
    }

    public async Task<PtyReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
#if NET
        var received = await _socket
            .ReceiveAsync(new Memory<byte>(buffer.Array!, buffer.Offset, buffer.Count), cancellationToken)
            .ConfigureAwait(false);
#else
        var received = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
#endif
        return new PtyReceiveResult(received.MessageType, received.Count, received.EndOfMessage);
    }

    public async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, CancellationToken cancellationToken)
    {
#if NET
        await _socket
            .SendAsync(
                new ReadOnlyMemory<byte>(buffer.Array!, buffer.Offset, buffer.Count),
                messageType,
                endOfMessage: true,
                cancellationToken)
            .ConfigureAwait(false);
#else
        await _socket
            .SendAsync(buffer, messageType, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
#endif
    }

    public Task CloseOutputAsync(CancellationToken cancellationToken) =>
        _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken);

    public void Dispose() => _socket.Dispose();
}
