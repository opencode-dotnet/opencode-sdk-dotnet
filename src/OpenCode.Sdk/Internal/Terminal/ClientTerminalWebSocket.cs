using System.Net.Http.Headers;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped <see cref="ITerminalWebSocket"/>: a thin adapter over <see cref="ClientWebSocket"/>.
/// On modern targets the upgrade uses the caller's handler when supplied; downlevel targets
/// keep the socket's own transport because they cannot accept an invoker. The
/// Basic credential rides the upgrade request's <c>Authorization</c> header, which is the
/// designed non-browser path: the API's authentication middleware skips credentials only for a
/// URL carrying a ticket, and the SDK never mints a ticket for its own connection (a single-use
/// credential in a URL that reaches logs is strictly worse than the header it already holds).
/// </summary>
internal sealed class ClientTerminalWebSocket : ITerminalWebSocket
{
    private readonly ClientWebSocket _socket;
#if NET
    private readonly HttpMessageInvoker? _invoker;
#endif
    private readonly Action<ClientWebSocket>? _onDisposed;
    private int _disposed;

    public ClientTerminalWebSocket(AuthenticationHeaderValue? authorization, HttpMessageHandler? httpHandler)
        : this(new ClientWebSocket(), authorization, null, httpHandler: httpHandler)
    {
    }

    internal ClientTerminalWebSocket(AuthenticationHeaderValue? authorization,
        IReadOnlyDictionary<string, string>? headers, Action<ClientWebSocket>? onDisposed, HttpMessageHandler? httpHandler = null)
        : this(new ClientWebSocket(), authorization, headers, onDisposed, httpHandler)
    {
    }

    internal ClientTerminalWebSocket(ClientWebSocket socket, AuthenticationHeaderValue? authorization,
        IReadOnlyDictionary<string, string>? headers, Action<ClientWebSocket>? onDisposed = null, HttpMessageHandler? httpHandler = null)
    {
        _socket = socket;
        _onDisposed = onDisposed;
        try
        {
            if (authorization is not null)
            {
                _socket.Options.SetRequestHeader("Authorization", authorization.ToString());
            }

#if NET
            // The pre-upgrade refusals the server answers (404, 401, 403) are only distinguishable
            // from the response status, which the socket keeps solely when asked to.
            _socket.Options.CollectHttpResponseDetails = true;
            _invoker = httpHandler is null ? null : new HttpMessageInvoker(httpHandler, disposeHandler: false);
#endif
            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    _socket.Options.SetRequestHeader(header.Key, header.Value);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
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
#if NET
            await (_invoker is null
                ? _socket.ConnectAsync(uri, cancellationToken)
                : _socket.ConnectAsync(uri, _invoker, cancellationToken)).ConfigureAwait(false);
#else
            await _socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
#endif
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // ClientWebSocket links the caller's token with its own; the caller's token names the cancellation.
            throw FailureClassification.CallerCancellation(
                exception, "The opencode terminal WebSocket upgrade was canceled.", cancellationToken);
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) is 0)
        {
            try
            {
                _socket.Dispose();
            }
            finally
            {
#if NET
                _invoker?.Dispose();
#endif
                _onDisposed?.Invoke(_socket);
            }
        }
    }
}
