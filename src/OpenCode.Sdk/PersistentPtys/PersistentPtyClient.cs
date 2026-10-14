using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>
/// A bound 'PersistentPtyClient' handle; it holds a <see cref="PersistentPtyRawClient"/> and the
/// <see cref="ConnectionSnapshot"/> the WebSocket door needs. The handle's public doors are
/// hand-written (ADR-0021) because the connect-token handshake needs a value the pinned document
/// does not carry and the live session is a WebSocket the document cannot describe; every
/// represented response still rides the generic envelope machinery, so route, status, and schema
/// drift breaks compilation through the raw twin.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public class PersistentPtyClient
{
    private readonly ConnectionSnapshot? _connection;
    private readonly string? _ptyId;
    private readonly PersistentPtyRawClient? _raw;

    internal PersistentPtyClient(PersistentPtyRawClient raw, ConnectionSnapshot connection, string ptyId)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ptyId);

        _raw = raw;
        _connection = connection;
        _ptyId = ptyId;
    }

    /// <summary>
    /// Initializes a mocking instance; members invoked without an override throw an instructive failure.
    /// </summary>
    protected PersistentPtyClient()
    {
    }

    /// <summary>
    /// Get one persistent terminal. 404 also answers when the daemon is not running.
    /// </summary>
    /// <param name="requestOptions">The per-call options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 'PersistentPtyResponse' envelope.</returns>
    /// <exception cref="OpenCodeApiException">The API returned an error status (declared: 400, 401, 404, 503) and NoThrow was not selected.</exception>
    /// <exception cref="OpenCodeTransportException">The server could not be reached or returned a malformed success body.</exception>
    /// <remarks>
    /// Operation <c>persistentPty.get</c>: <c>GET /api/experimental/persistent-pty/{ptyID}</c>.
    /// </remarks>
    public virtual Task<PersistentPtyResponse> GetAsync(
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
        Raw.GetAsync(requestOptions, cancellationToken);

    /// <summary>
    /// Resize one persistent terminal; the resize also selects it as the session's current terminal.
    /// </summary>
    /// <param name="request">The request body.</param>
    /// <param name="requestOptions">The per-call options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 'PersistentPtyUpdateResponse' envelope.</returns>
    /// <exception cref="OpenCodeApiException">The API returned an error status (declared: 400, 401, 404, 503) and NoThrow was not selected.</exception>
    /// <exception cref="OpenCodeTransportException">The server could not be reached or returned a malformed success body.</exception>
    /// <remarks>
    /// Operation <c>persistentPty.update</c>: <c>PUT /api/experimental/persistent-pty/{ptyID}</c>.
    /// </remarks>
    public virtual Task<PersistentPtyUpdateResponse> UpdateAsync(PersistentPtyUpdateRequest request,
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
        Raw.UpdateAsync(request, requestOptions, cancellationToken);

    /// <summary>
    /// Terminate and remove one persistent terminal.
    /// </summary>
    /// <param name="requestOptions">The per-call options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 'PersistentPtyRemoveResponse' envelope.</returns>
    /// <exception cref="OpenCodeApiException">The API returned an error status (declared: 400, 401, 404, 503) and NoThrow was not selected.</exception>
    /// <exception cref="OpenCodeTransportException">The server could not be reached or returned a malformed success body.</exception>
    /// <remarks>
    /// Operation <c>persistentPty.remove</c>: <c>DELETE /api/experimental/persistent-pty/{ptyID}</c>.
    /// </remarks>
    public virtual Task<PersistentPtyRemoveResponse> RemoveAsync(
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
        Raw.RemoveAsync(requestOptions, cancellationToken);

    /// <summary>
    /// Snapshot one persistent terminal: its info, the retained text, the screen checkpoint as
    /// the terminal-escape bytes an emulator sized to <c>Info.Size</c> replays, and the cursor.
    /// </summary>
    /// <param name="requestOptions">The per-call options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 'PersistentPtySnapshotResponse' envelope.</returns>
    /// <exception cref="OpenCodeApiException">The API returned an error status (declared: 400, 401, 404, 503) and NoThrow was not selected.</exception>
    /// <exception cref="OpenCodeTransportException">The server could not be reached or returned a malformed success body.</exception>
    /// <remarks>
    /// Operation <c>persistentPty.snapshot</c>: <c>GET /api/experimental/persistent-pty/{ptyID}/snapshot</c>.
    /// </remarks>
    public virtual Task<PersistentPtySnapshotResponse> GetSnapshotAsync(
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
        Raw.GetSnapshotAsync(requestOptions, cancellationToken);

    /// <summary>
    /// Create a short-lived single-use ticket for a browser's WebSocket upgrade. The ticket header
    /// the handler requires is applied internally and is never a caller's argument; the ticket is
    /// scoped to this terminal only.
    /// </summary>
    /// <param name="requestOptions">The per-call options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The 'PersistentPtyConnectTokenResponse' envelope.</returns>
    /// <exception cref="OpenCodeApiException">The API returned an error status (declared: 400, 401, 403, 404, 503) and NoThrow was not selected.</exception>
    /// <exception cref="OpenCodeTransportException">The server could not be reached or returned a malformed success body.</exception>
    /// <remarks>
    /// Operation <c>persistentPty.connectToken</c>: <c>POST /api/experimental/persistent-pty/{ptyID}/connect-token</c>.
    /// </remarks>
    public virtual Task<PersistentPtyConnectTokenResponse> CreateConnectTokenAsync(
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
        Raw.CreateConnectTokenAsync(xOpencodeTicket: PtyTicketHeader.Sentinel, requestOptions, cancellationToken);

    /// <summary>
    /// Opens the terminal's live WebSocket session and returns once the server has attached this
    /// connection. The upgrade is the SDK's transport divergence: it builds its own socket, so a
    /// caller-supplied <see cref="HttpClient"/>, its proxy, and its handler chain do not apply.
    /// The client's Basic credential rides the upgrade request's <c>Authorization</c> header — the
    /// designed non-browser path — and the SDK never mints a ticket for its own connection;
    /// <see cref="CreateConnectTokenAsync"/> stays the door for handing a browser one. Unlike the
    /// normal PTY family, the server does not check the terminal's existence before upgrading: a
    /// missing terminal or an absent daemon closes 4404 right after, which this door surfaces here
    /// rather than on the first read.
    /// </summary>
    /// <param name="options">The connect options: the replay cursor, the role, the attachment identity, and the takeover.</param>
    /// <param name="cancellationToken">The cancellation token bounding the upgrade and the attach.</param>
    /// <returns>The live session, already attached; the caller owns its disposal.</returns>
    /// <exception cref="OpenCodeTransportException">
    /// The upgrade was refused, never completed, or the server closed before attaching. A platform
    /// that cannot construct the underlying
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/> (pre-Windows-8) maps here too, naming
    /// the platform as the cause, rather than escaping as a raw
    /// <see cref="PlatformNotSupportedException"/>.
    /// </exception>
    /// <remarks>
    /// Operation <c>persistentPty.connect</c>: <c>GET /api/experimental/persistent-pty/{ptyID}/connect</c>.
    /// </remarks>
    public virtual async Task<PersistentPtySession> ConnectAsync(PersistentPtyConnectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var connection = Connection;
        var ptyId = PtyId;
        var address = PersistentPtyConnectUriBuilder.Build(connection, ptyId, options);

        // Ownership stays local until the session has taken the socket over: an upgrade that
        // completed but never attached must not leave the connection open behind it.
        ClientTerminalWebSocket? socket = null;
        try
        {
            socket = CreateSocket(connection, ptyId);
            await socket
                .ConnectAsync(address, ptyId, PersistentPtyUpgradeFailurePolicy.Instance, cancellationToken)
                .ConfigureAwait(false);
            var session = await PersistentPtySession.AttachAsync(
                socket, ptyId, options?.SendTimeout ?? TerminalSocketBounds.DefaultSendTimeout, cancellationToken)
                .ConfigureAwait(false);
            socket = null;
            return session;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static ClientTerminalWebSocket CreateSocket(ConnectionSnapshot connection, string ptyId)
    {
        try
        {
            return new ClientTerminalWebSocket(connection.Authorization);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new OpenCodeTransportException(
                $"The opencode persistent PTY '{ptyId}' WebSocket could not be constructed on this platform.", exception);
        }
    }

    private ConnectionSnapshot Connection => _connection ?? throw MockSeam.CreateError("PersistentPtyClient", "Snapshot");

    private string PtyId => _ptyId ?? throw MockSeam.CreateError("PersistentPtyClient", "PtyId");

    private PersistentPtyRawClient Raw => _raw ?? throw MockSeam.CreateError("PersistentPtyClient", "RawClient");
}
