using System.Buffers;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// One buffered response body over a pooled array, still encoded as the wire sent it.
/// Written by <see cref="ResponseBufferingPolicy"/>, consumed by
/// <see cref="ResponseMaterializer"/>; the array goes back to its pool through
/// <see cref="PipelineMessage.Dispose"/>, never earlier, so no consumer can observe a
/// recycled buffer.
/// </summary>
internal sealed class ResponseBody : IDisposable
{
    private readonly ArrayPool<byte>? _pool;
    private byte[]? _bytes;

    public ResponseBody(ArrayPool<byte> pool, byte[] bytes, int length)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, bytes.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        _pool = pool;
        _bytes = bytes;
        Length = length;
    }

    private ResponseBody()
    {
        _bytes = [];
    }

    /// <summary>Gets the empty body; unpooled, so disposing it is a no-op.</summary>
    public static ResponseBody Empty { get; } = new();

    /// <summary>Gets the count of body bytes; the backing array may be longer.</summary>
    public int Length { get; }

    /// <summary>Gets the backing array; only the first <see cref="Length"/> bytes are the body.</summary>
    public byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(ResponseBody));

    public void Dispose()
    {
        if (_pool is { } pool && _bytes is { } bytes)
        {
            _bytes = null;

            // Parity with the runtime's own pooled response buffering: the array is not
            // cleared on return; a response body is not treated as a secret against the
            // process's own memory.
            pool.Return(bytes);
        }
    }
}
