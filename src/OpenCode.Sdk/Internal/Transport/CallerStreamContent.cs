using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// A raw-byte request body over the caller's own stream. The caller owns the stream: it is read
/// once, from its current position to its end, and never disposed, rewound, or buffered whole. A
/// second serialization — a redirect or a retry in a caller-supplied handler — is refused by name,
/// because the bytes already sent cannot be read again. Each chunk written restarts the message's
/// progress window, so a slow upload that keeps moving is bounded the way a slow download is: by
/// progress, not by its total length.
/// </summary>
internal sealed class CallerStreamContent : HttpContent
{
    private const int ChunkSize = 81_920;
    private static readonly MediaTypeHeaderValue OctetStream = new("application/octet-stream");

    private readonly Stream _source;
    private readonly PipelineMessage _message;
    private bool _serialized;

    public CallerStreamContent(Stream source, PipelineMessage message)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(message);

        if (!source.CanRead)
        {
            throw new ArgumentException("The request body stream must be readable.", nameof(source));
        }

        _source = source;
        _message = message;
        Headers.ContentType = OctetStream;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        CopyAsync(stream, _message.NetworkToken);

#if NET
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        CopyAsync(stream, cancellationToken);
#endif

    /// <summary>A seekable stream declares what is left of it; any other is sent chunked.</summary>
    protected override bool TryComputeLength(out long length)
    {
        if (_source.CanSeek)
        {
            length = _source.Length - _source.Position;
            return true;
        }

        length = 0;
        return false;
    }

    private async Task CopyAsync(Stream target, CancellationToken cancellationToken)
    {
        if (_serialized)
        {
            throw new InvalidOperationException(
                "The request body stream was already sent; the SDK neither rewinds nor re-reads a caller's stream.");
        }

        _serialized = true;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int read;
            while ((read = await _source.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                _message.RestartProgressWindow();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
