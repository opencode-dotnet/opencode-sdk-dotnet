using System.Globalization;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// One reconnecting stream attempt: before establishment the connect bound distinguishes no response
/// from a response without a first event; once established, every byte restarts the idle window.
/// The give-up window and caller's token cut the attempt too.
/// </summary>
internal sealed class StreamAttempt : IDisposable
{
    private readonly ProgressWindow _window;
    private readonly TimeSpan _idleWindow;
    private bool? _startedConnected;
    // A plain field: the send's continuation writes it, and Describe reads it only after awaiting
    // the attempt's enumeration that ran that send, so the await orders the write before the read.
    private bool _responseReceived;

    /// <summary>Starts an attempt; its connect bound runs from now.</summary>
    /// <param name="time">The clock the windows run on.</param>
    /// <param name="idleWindow">How long it may go without a byte once established.</param>
    /// <param name="giveUpToken">The give-up window's token, linked over the caller's.</param>
    public StreamAttempt(TimeProvider time, TimeSpan idleWindow, CancellationToken giveUpToken)
    {
        _idleWindow = idleWindow;
        _window = new ProgressWindow(idleWindow, time, giveUpToken);
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
            throw new OpenCodeTransportException($"The opencode event stream did not start with {StreamReconnect.ConnectedEventType}.");
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

    /// <summary>Stops the idle window while the consumer holds an event; the server's silence is not measured then.</summary>
    public void Pause() => _window.Stop();

    /// <summary>Starts the idle window again once the consumer asks for the next event.</summary>
    public void Resume() => _window.Restart();

    /// <summary>
    /// Names a cancellation that cut the attempt while neither the caller nor the give-up window
    /// asked for it: the attempt's own window passed, which surfaces as a transport failure saying
    /// which window it was, never as a cancellation.
    /// </summary>
    /// <param name="cancellation">The cancellation the attempt's read ended with.</param>
    /// <returns>The failure to report.</returns>
    public OpenCodeTransportException Describe(OperationCanceledException cancellation)
    {
        var window = IsEstablished ? _idleWindow : StreamReconnect.ConnectBound;
        var seconds = window.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture);
        var description = (IsEstablished, _responseReceived) switch
        {
            (true, _) => $"The opencode event stream received nothing for {seconds} seconds.",
            (_, true) => $"The opencode server answered the event stream request but sent no event within {seconds} seconds.",
            _ => $"The opencode server did not answer the event stream request within {seconds} seconds.",
        };
        return new OpenCodeTransportException(description, cancellation);
    }

    public void Dispose() => _window.Dispose();
}
