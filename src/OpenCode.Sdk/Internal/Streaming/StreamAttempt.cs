namespace OpenCode.Sdk.Internal;

/// <summary>
/// One reconnecting stream attempt: before establishment the connect bound distinguishes no response
/// from a response without a first event; once established, every byte restarts the idle window.
/// Its owning source's token cuts the attempt too; subscriber tokens never reach physical I/O.
/// </summary>
internal sealed class StreamAttempt : IDisposable
{
    private readonly ProgressWindow _window;
    private readonly TimeSpan _idleWindow;
    private bool? _startedConnected;
    /// <summary>
    /// Whether the server answered this attempt. A plain field: the send's continuation writes it,
    /// and <see cref="Describe"/> reads it only after awaiting the enumeration that ran that send, so
    /// the await orders the write before the read.
    /// </summary>
    private bool _responseReceived;

    /// <summary>Starts an attempt; its connect bound runs from now.</summary>
    /// <param name="time">The clock the windows run on.</param>
    /// <param name="idleWindow">How long it may go without a byte once established.</param>
    /// <param name="sourceToken">The owning source's cancellation token.</param>
    public StreamAttempt(TimeProvider time, TimeSpan idleWindow, CancellationToken sourceToken)
    {
        _idleWindow = idleWindow;
        _window = new ProgressWindow(idleWindow, time, sourceToken);
        _window.Restart(StreamReconnect.ConnectBound);
    }

    /// <summary>Gets the token the attempt's network I/O runs under.</summary>
    public CancellationToken Token => _window.Token;

    /// <summary>Gets whether the attempt has been established on its <c>server.connected</c>.</summary>
    public bool IsEstablished { get; private set; }

    /// <summary>Records that the server answered, even when no first event follows its headers.</summary>
    public void OnResponse() => _responseReceived = true;

    /// <summary>Reports a frame the attempt received; only the first decides whether it can be established.</summary>
    /// <param name="frame">The frame, as the wire wrote it.</param>
    public void OnFrame(ServerSentEvent frame) => _startedConnected ??= StreamReconnect.IsConnected(frame);

    /// <summary>
    /// Establishes the attempt once its first event has been read: the idle window takes over from
    /// the connect bound. An attempt whose first frame was not <c>server.connected</c> fails, as the
    /// pinned client fails it.
    /// </summary>
    public void Establish()
    {
        if (_startedConnected is not true)
        {
            throw FailureClassification.EventStreamFirstFrame();
        }

        IsEstablished = true;
        _window.Restart();
    }

    /// <summary>Reports bytes the attempt received: they start the idle window again once established.</summary>
    public void OnActivity()
    {
        if (IsEstablished)
        {
            _window.Restart();
        }
    }

    /// <summary>
    /// Names a cancellation that cut the attempt while its source did not ask for it:
    /// the attempt's own window passed, which surfaces as a transport failure saying
    /// which window it was, never as a cancellation.
    /// </summary>
    /// <param name="cancellation">The cancellation the attempt's read ended with.</param>
    /// <returns>The failure to report.</returns>
    public OpenCodeTransportException Describe(OperationCanceledException cancellation) =>
        FailureClassification.EventStreamWindow(cancellation,
            IsEstablished ? _idleWindow : StreamReconnect.ConnectBound, IsEstablished, _responseReceived);

    public void Dispose() => _window.Dispose();
}
