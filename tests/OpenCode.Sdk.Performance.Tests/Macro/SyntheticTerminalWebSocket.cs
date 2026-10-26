using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Macro;

/// <summary>Produces fixed-size terminal messages and signals when their decoded frames are queued.</summary>
/// <remarks>
/// Close waits for the receive acknowledgement, so the pending receive has passed its close wait before the session can dispose the socket.
/// A later failure inside the product pump's decode never passes through this socket, so it cannot complete the barrier, and the 'x' payload cannot produce one.
/// </remarks>
internal sealed class SyntheticTerminalWebSocket : ITerminalWebSocket
{
    private const int SampleCount = 50;
    private const int MinimumSampleInterval = 1;

    private readonly byte[] _payload;
    private readonly WebSocketMessageType _messageType;
    private readonly int _count;
    private readonly int _sampleEvery;
    private readonly Action<int> _sample;
    private readonly Channel<bool> _start = Channel.CreateUnbounded<bool>();
    private readonly Channel<bool> _close = Channel.CreateUnbounded<bool>();
    private readonly Channel<bool> _closeReceived = Channel.CreateUnbounded<bool>();
    private readonly Channel<bool> _barrier = Channel.CreateUnbounded<bool>();
    private int _delivered;
    private int _requests;
    private int _offset;
    private int _closed;
    private int _disposed;
    private int _sampled;
    private bool _started;

    public SyntheticTerminalWebSocket(int size, WebSocketMessageType messageType, int count, Action<int> sample)
    {
        _payload = new byte[size];
        _payload.AsSpan().Fill((byte)'x');
        _messageType = messageType;
        _count = count;
        _sampleEvery = Math.Max(MinimumSampleInterval, count / SampleCount);
        _sample = sample;
    }

    public int Delivered => Volatile.Read(ref _delivered);

    public int ReceiveRequests => Volatile.Read(ref _requests);

    public WebSocketCloseStatus? CloseStatus => Volatile.Read(ref _closed) == 1 ? WebSocketCloseStatus.NormalClosure : null;

    public string? CloseStatusDescription => null;

    public void OpenGate() => _ = _start.Writer.TryWrite(true);

    /// <summary>Waits until every message is queued. The macro has no cancellation input, so the wait ends at the barrier or with the failure the receive path reports.</summary>
    public Task WaitForEnqueueBarrierAsync() => WaitForSignalAsync(_barrier, CancellationToken.None);

    public Task<PtyReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var delivered = Delivered;
        if (delivered > _sampled && delivered % _sampleEvery == 0)
        {
            try
            {
                _sample(delivered);
                _sampled = delivered;
            }
            catch (Exception exception)
            {
                _ = _barrier.Writer.TryComplete(exception);
                throw;
            }
        }

        if (delivered == _count)
        {
            _ = _barrier.Writer.TryWrite(true);
        }

        return Task.Run(() => DeliverAsync(buffer, cancellationToken), cancellationToken);
    }

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The receive-queue macro sends no input.");

    public async Task CloseOutputAsync(CancellationToken cancellationToken)
    {
        SignalClose();
        await WaitForSignalAsync(_closeReceived, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = _start.Writer.TryComplete();
            _ = _close.Writer.TryComplete();
            _ = _closeReceived.Writer.TryComplete();
            _ = _barrier.Writer.TryComplete();
        }
    }

    private async Task<PtyReceiveResult> DeliverAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_started)
        {
            await WaitForSignalAsync(_start, cancellationToken).ConfigureAwait(false);
            _started = true;
        }

        if (Delivered == _count)
        {
            await WaitForSignalAsync(_close, cancellationToken).ConfigureAwait(false);
            _ = _closeReceived.Writer.TryWrite(true);
            return new PtyReceiveResult(WebSocketMessageType.Close, 0, EndOfMessage: true);
        }

        var length = Math.Min(_payload.Length - _offset, buffer.Count);
        _payload.AsSpan(_offset, length).CopyTo(buffer.AsSpan());
        _offset += length;
        var end = _offset == _payload.Length;
        if (end)
        {
            _offset = 0;
            Interlocked.Increment(ref _delivered);
        }

        return new PtyReceiveResult(_messageType, length, end);
    }

    private void SignalClose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _ = _close.Writer.TryWrite(true);
        }
    }

    private async Task WaitForSignalAsync(Channel<bool> signal, CancellationToken cancellationToken)
    {
        try
        {
            _ = await signal.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception) when (exception.InnerException is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        catch (ChannelClosedException exception) when (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException("The synthetic terminal socket is disposed.", exception);
        }
    }
}
