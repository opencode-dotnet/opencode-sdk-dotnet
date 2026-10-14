using System.Net;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Delivers a payload in delayed chunks through a live content stream, so a progress window
/// shorter than the whole body but longer than each gap sees a slow-but-flowing read.
/// </summary>
internal sealed class TricklingContent : HttpContent
{
    private readonly byte[] _payload;
    private readonly int _chunkCount;
    private readonly TimeSpan _gap;

    public TricklingContent(byte[] payload, int chunkCount, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCount, 1);

        _payload = payload;
        _chunkCount = chunkCount;
        _gap = gap;
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new TricklingStream(_payload, _chunkCount, _gap));

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new NotSupportedException("Read this content through its live stream.");

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    private sealed class TricklingStream : Stream
    {
        private readonly byte[] _payload;
        private readonly int _chunkSize;
        private readonly TimeSpan _gap;
        private int _position;

        public TricklingStream(byte[] payload, int chunkCount, TimeSpan gap)
        {
            _payload = payload;
            _chunkSize = Math.Max(1, payload.Length / chunkCount);
            _gap = gap;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return await ReadChunkAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await ReadChunkAsync(buffer, cancellationToken);
#endif

        [SlopwatchSuppress("SW004", "The delay is the subject under test: it paces a trickling body against the progress window")]
        private async Task<int> ReadChunkAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_position >= _payload.Length)
            {
                return 0;
            }

            await Task.Delay(_gap, cancellationToken);
            var read = Math.Min(_chunkSize, Math.Min(buffer.Length, _payload.Length - _position));
            _payload.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The trickling body is read asynchronously.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
