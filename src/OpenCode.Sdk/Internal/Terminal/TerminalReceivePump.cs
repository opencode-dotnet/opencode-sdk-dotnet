using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace OpenCode.Sdk.Internal;

/// <summary>Owns physical receive, fragment assembly, and undelivered frames for one connection.</summary>
/// <typeparam name="TFrame">The decoded frame type.</typeparam>
internal sealed class TerminalReceivePump<TFrame> : IAsyncDisposable
    where TFrame : class
{
    private readonly Channel<TFrame> _frames = Channel.CreateUnbounded<TFrame>(new UnboundedChannelOptions
    {
        SingleReader = false,
        AllowSynchronousContinuations = false,
    });
    private readonly TerminalCancellationSource _lifetime = new();
    private readonly Lock _sync = new();
    private readonly Task _completion;
    private bool _abandoned;
    private TerminalConnectionOutcome? _outcome;

    public TerminalReceivePump(
        ITerminalWebSocket socket,
        ITerminalFrameDecoder<TFrame> decoder,
        ITerminalClosePolicy closePolicy,
        Func<Task> onEnded)
    {
        _completion = ReceiveAsync(socket, decoder, closePolicy, onEnded);
    }

    /// <summary>Gets whether the connection has reached its terminal outcome.</summary>
    public TerminalConnectionOutcome? Outcome
    {
        get
        {
            lock (_sync)
            {
                return _outcome;
            }
        }
    }

    /// <summary>Gets the first terminal failure, if any.</summary>
    public Exception? Failure
    {
        get
        {
            lock (_sync)
            {
                return _outcome?.Failure;
            }
        }
    }

    /// <summary>Selects a frame atomically with cancellation observation and disposal.</summary>
    public bool TryRead(CancellationToken cancellationToken, [NotNullWhen(true)] out TFrame? frame)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame = null;
            return !_abandoned && _frames.Reader.TryRead(out frame);
        }
    }

    /// <summary>Waits only for consumer progress; this token never reaches physical receive.</summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _frames.Reader.WaitToReadAsync(cancellationToken);

    /// <summary>Records the first terminal outcome and wakes consumers after the queued prefix.</summary>
    public void Complete(Exception? failure)
    {
        lock (_sync)
        {
            if (_outcome is not null)
            {
                return;
            }

            _outcome = new TerminalConnectionOutcome(failure);
            _ = _frames.Writer.TryComplete();
        }
    }

    /// <summary>Prevents publication and releases unread frames without waiting for a consumer.</summary>
    public void Abandon()
    {
        lock (_sync)
        {
            _abandoned = true;
            _outcome = new TerminalConnectionOutcome(null);
            _ = _frames.Writer.TryComplete();
            while (_frames.Reader.TryRead(out _))
            {
                // Explicit disposal abandons only frames that no reader has selected.
            }
        }
    }

    /// <summary>Stops and observes the owned receiver before releasing its cancellation source.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            // The receiver is owned here and every await in it declines context capture.
#pragma warning disable VSTHRD003 // Joining this connection's receiver is the disposal contract.
            await _completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task ReceiveAsync(
        ITerminalWebSocket socket,
        ITerminalFrameDecoder<TFrame> decoder,
        ITerminalClosePolicy closePolicy,
        Func<Task> onEnded)
    {
        try
        {
            await ReceiveMessagesAsync(socket, decoder, closePolicy).ConfigureAwait(false);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.PtyWebSocketRead))
        {
            Complete(FailureClassification.Map(exception, FailurePhase.PtyWebSocketRead, CancellationToken.None));
        }
        catch (OpenCodeTransportException exception)
        {
            Complete(exception);
        }
        catch (Exception exception)
        {
            Complete(exception);
            throw;
        }
        finally
        {
            await onEnded().ConfigureAwait(false);
        }
    }

    private async Task ReceiveMessagesAsync(
        ITerminalWebSocket socket,
        ITerminalFrameDecoder<TFrame> decoder,
        ITerminalClosePolicy closePolicy)
    {
        var buffer = new byte[TerminalSocketBounds.ReceiveBufferSize];
        var segment = new ArraySegment<byte>(buffer);
        PtyMessageAssembler? assembly = null;
        while (Outcome is null)
        {
            var received = await socket.ReceiveAsync(segment, _lifetime.Token).ConfigureAwait(false);
            if (received.MessageType is WebSocketMessageType.Close)
            {
                Complete(closePolicy.Map(socket.CloseStatus, socket.CloseStatusDescription));
                return;
            }

            if (!received.EndOfMessage)
            {
                assembly ??= new PtyMessageAssembler();
                assembly.Append(buffer, received.Count);
                continue;
            }

            TFrame frame;
            if (assembly is null || assembly.Length is 0)
            {
                frame = decoder.Decode(received.MessageType, buffer, received.Count);
            }
            else
            {
                assembly.Append(buffer, received.Count);
                frame = decoder.Decode(received.MessageType, assembly.Buffer, assembly.Length);
                assembly.Reset();
            }

            lock (_sync)
            {
                if (_outcome is null)
                {
                    _ = _frames.Writer.TryWrite(frame);
                }
            }
        }
    }
}
