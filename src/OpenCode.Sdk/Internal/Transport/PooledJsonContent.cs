#if !NETSTANDARD2_0
using System.Buffers;
using System.Net;

namespace OpenCode.Sdk.Internal;

/// <summary>A JSON request body in a rented array, which goes back to the pool only when nothing can still read it.</summary>
/// <remarks>
/// A write that completes leaves the content clean. A write that hasn't completed when the content is disposed, a write
/// that faulted, or two writes at once leave the array to the garbage collector: a reused array could send another
/// request's bytes. A handler can still send the body again after a failure, because the array stays intact. A returned
/// array is cleared, since a request body can hold credentials and typed text. This relies on the contract of
/// <c>Stream.WriteAsync</c> and <c>Stream.Write</c>: a completed write no longer uses the memory. On .NET Framework, the
/// overload with no token matches ByteArrayContent's, which has none either.
/// </remarks>
internal sealed class PooledJsonContent : HttpContent
{
    private const int Clean = 0;
    private const int Writing = 1;
    private const int Disposed = 2;

    private readonly ArrayPool<byte> _pool;
    private readonly byte[] _buffer;
    private readonly int _length;
    private int _state;
    private int _tainted;

    public PooledJsonContent(ArrayPool<byte> pool, byte[] buffer, int length)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(buffer);

        _pool = pool;
        _buffer = buffer;
        _length = length;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WriteAsync(stream, CancellationToken.None);

#if NET
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        WriteAsync(stream, cancellationToken);

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var first = BeginWrite();
        stream.Write(_buffer, 0, _length);
        EndWrite(first);
    }
#endif

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _state, Disposed) == Clean && Volatile.Read(ref _tainted) == 0)
        {
            _buffer.AsSpan(0, _length).Clear();
            _pool.Return(_buffer);
        }

        base.Dispose(disposing);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = BeginWrite();
        await stream.WriteAsync(_buffer.AsMemory(0, _length), cancellationToken).ConfigureAwait(false);
        EndWrite(first);
    }

    private bool BeginWrite()
    {
        var first = Interlocked.CompareExchange(ref _state, Writing, Clean) == Clean;
        if (!first)
        {
            // An earlier write faulted or is still running, so the array stays out of the pool for good. Each side writes
            // its own field, interlocked, before reading the other's, so this write and a dispose can't both miss.
            _ = Interlocked.Exchange(ref _tainted, 1);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == Disposed, this);
        }

        return first;
    }

    private void EndWrite(bool first)
    {
        if (first)
        {
            _ = Interlocked.CompareExchange(ref _state, Clean, Writing);
        }
    }
}
#endif
