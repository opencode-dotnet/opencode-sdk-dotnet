using System.Globalization;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// One connection attempt of a reconnecting stream and the window it runs under. Until the attempt
/// is established it has <see cref="StreamReconnect.ConnectBound"/> to answer and deliver its first
/// event, whatever arrives meanwhile; once established, every byte starts the client's idle window
/// again. The window is linked over the give-up window, so the caller's token and the give-up both
/// cut the attempt too.
/// </summary>
internal sealed class StreamAttempt : IDisposable
{
    private readonly ProgressWindow _window;
    private readonly TimeSpan _idleWindow;
    private bool? _startedConnected;

    /// <summary>Starts an attempt; its connect bound runs from now.</summary>
    /// <param name="time">The clock the windows run on.</param>
    /// <param name="idleWindow">How long the attempt may go without a byte once established.</param>
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
        return new OpenCodeTransportException(IsEstablished
            ? $"The opencode event stream received nothing for {seconds} seconds."
            : $"The opencode event stream did not connect within {seconds} seconds.", cancellation);
    }

    public void Dispose() => _window.Dispose();
}
