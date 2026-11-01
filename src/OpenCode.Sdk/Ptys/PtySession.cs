using System.Net.WebSockets;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// A live pseudo-terminal connection: read the frames the server sends, write the input the
/// terminal receives, and dispose to close. The session owns its socket, so disposing it is the
/// only way to end the connection; the process exit code is never on this wire — a reader that
/// needs it asks <see cref="PtyClient.GetAsync"/>.
/// </summary>
public class PtySession : IAsyncDisposable
{
    private readonly TerminalSocketCore<PtyFrame>? _core;

    internal PtySession(TerminalSocketCore<PtyFrame> core)
    {
        _core = core;
    }

    internal PtySession(ITerminalWebSocket socket, TimeProvider time, TimeSpan? sendTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(socket);

        _core = new TerminalSocketCore<PtyFrame>(
            socket,
            PtyFrameDecoder.Instance,
            PtyClosePolicy.Instance,
            typeof(PtySession),
            time)
        {
            SendTimeout = sendTimeout ?? TerminalSocketBounds.DefaultSendTimeout,
        };
    }

    /// <summary>
    /// Initializes a mocking instance; members invoked without an override throw an instructive failure.
    /// </summary>
    protected PtySession()
    {
    }

    private TerminalSocketCore<PtyFrame> Core => _core ?? throw MockSeam.CreateError("PtySession", "WebSocket");

    /// <summary>
    /// Reads the frames the server sends until it closes the connection normally. One session
    /// carries one active enumeration: undelivered frames have one consumer, so a second
    /// concurrent enumeration is refused. Reading after disposal is not an error: unlike
    /// <see cref="WriteAsync"/>, which throws once disposed, the enumeration simply ends empty.
    /// Canceling or disposing an enumerator ends only that consumer; another enumeration may
    /// continue on the same connection. Unread frames remain queued without a fixed memory limit.
    /// Peer closure drains the queue before its outcome; explicit session disposal abandons
    /// unread frames and awaits owned I/O cleanup.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token ending the read.</param>
    /// <returns>The frames, in the order the server sent them.</returns>
    /// <exception cref="InvalidOperationException">A read enumeration is already active on this session.</exception>
    /// <exception cref="OpenCodeTransportException">The connection failed, closed abnormally, or carried an unreadable control frame.</exception>
    public virtual IAsyncEnumerable<PtyFrame> ReadAsync(CancellationToken cancellationToken = default) =>
        Core.ReadAsync(cancellationToken);

    /// <summary>
    /// Writes input to the pseudo-terminal as one UTF-8 text message, exactly as given: this is
    /// the raw door, for partial input, control sequences, and bytes already produced by a
    /// terminal emulator. A terminal's Enter key is carriage return (<c>\r</c>), so input ending
    /// in <c>\n</c> renders the text but never submits it — to run one command, call
    /// <see cref="SubmitAsync"/>, which adds that terminator. Sends are serialized: the socket
    /// allows one outstanding send, so concurrent callers queue rather than corrupt the stream.
    /// Unlike <see cref="ReadAsync"/>, which yields an empty enumeration after disposal, a write
    /// after disposal throws rather than doing nothing.
    /// </summary>
    /// <param name="input">The input to send; never null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the message is sent.</returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="OpenCodeTransportException">The connection failed while sending.</exception>
    public virtual Task WriteAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        return SendInputAsync(input, cancellationToken);
    }

    /// <summary>
    /// Submits one line to the pseudo-terminal: the line plus the carriage return a terminal's
    /// Enter key sends, as one UTF-8 text message. This is the door for running a command; the
    /// line is sent exactly as given otherwise, with no trimming and no other transformation. An
    /// empty line is a bare Enter. Sends are serialized exactly as <see cref="WriteAsync"/>'s
    /// are, and a submit after disposal throws just as a write does.
    /// </summary>
    /// <param name="line">Exactly one line, carrying no carriage return and no line feed; never null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the message is sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="line"/> carries a carriage return or a line feed: a submit is one Enter, so send a break with <see cref="WriteAsync"/>.</exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed.</exception>
    /// <exception cref="OpenCodeTransportException">The connection failed while sending.</exception>
    public virtual Task SubmitAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        var submitted = TerminalSubmission.Compose(line, nameof(line));
        return SendInputAsync(submitted, cancellationToken);
    }

    private Task SendInputAsync(string input, CancellationToken cancellationToken)
    {
        var core = Core;
        return core.SendAsync(() =>
        {
            var length = Encoding.UTF8.GetByteCount(input);
            var payload = core.SendPool.Rent(length);
            _ = Encoding.UTF8.GetBytes(input, 0, input.Length, payload, 0);
            return new ArraySegment<byte>(payload, 0, length);
        }, WebSocketMessageType.Text, TerminalSendActions.Rented, cancellationToken);
    }

    /// <summary>
    /// Closes the connection: a graceful close first, bounded so an unresponsive peer cannot
    /// stall the caller, then the socket's hard teardown. Disposal is idempotent, and a read
    /// waiting on the socket ends as a normal end rather than a fault.
    /// </summary>
    /// <returns>A task that completes once the connection is closed.</returns>
    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_core is not null)
        {
            await _core.DisposeAsync().ConfigureAwait(false);
        }
    }
}
