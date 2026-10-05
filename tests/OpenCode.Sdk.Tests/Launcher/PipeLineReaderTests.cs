using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Launcher;

/// <summary>
/// The POSIX line reader against prepared streams: lines split the way <c>Process</c>'s readers
/// split them, across read boundaries, and a reader whose read never completes ends when it is
/// released, the way disposal releases a pipe a descendant still holds.
/// </summary>
public sealed class PipeLineReaderTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Completion_Should_Deliver_Every_Line_Split_On_Each_Terminator_Across_Reads()
    {
        var lines = new List<string>();
        using var stream = ChunkedStream.Of("first\r", "\nsecond\rthird\n", "\n", "unterminated");
        var reader = PipeLineReader.Start(stream, lines.Add);

        var endOfStream = await reader.Completion.WaitAsync(Bound);
        await reader.DisposeAsync();

        await Assert.That(endOfStream).IsTrue();
        await Assert.That(lines).IsEquivalentTo(["first", "second", "third", "", "unterminated"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DisposeAsync_Should_End_A_Read_That_Never_Completes()
    {
        var lines = new List<string>();
        using var stream = new HeldPipeStream("before release\n"u8.ToArray());
        var reader = PipeLineReader.Start(stream, lines.Add);
        await LiveReadiness.WaitAsync(_ => Task.FromResult(lines.Count), static count => count == 1, "the first line", CancellationToken.None);

        await reader.DisposeAsync();
        var endOfStream = await reader.Completion.WaitAsync(Bound);

        await Assert.That(endOfStream).IsFalse();
        await Assert.That(lines).IsEquivalentTo(["before release"], CollectionOrdering.Matching);
        await Assert.That(stream.Disposed).IsTrue();
    }

    /// <summary>
    /// A pipe whose writer stays open: one prefix, then a read that only a canceled token or the
    /// stream's own closing ends, the two ways a release reaches a pending pipe read.
    /// </summary>
    private sealed class HeldPipeStream : Stream
    {
        private readonly byte[] _prefix;
        private TaskCompletionSource<int>? _pending;
        private int _offset;

        public HeldPipeStream(byte[] prefix) => _prefix = prefix;

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadCoreAsync(buffer, offset, count, cancellationToken);

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var array = new byte[buffer.Length];
            var read = await ReadCoreAsync(array, 0, array.Length, cancellationToken);
            array.AsSpan(0, read).CopyTo(buffer.Span);
            return read;
        }
#endif

        private async Task<int> ReadCoreAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_offset < _prefix.Length)
            {
                var length = Math.Min(_prefix.Length - _offset, count);
                Array.Copy(_prefix, _offset, buffer, offset, length);
                _offset += length;
                return length;
            }

            var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _pending, pending);
            if (Disposed)
            {
                _ = pending.TrySetException(new ObjectDisposedException(nameof(HeldPipeStream)));
            }

            using var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
            return await pending.Task;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _ = Volatile.Read(ref _pending)?.TrySetException(new ObjectDisposedException(nameof(HeldPipeStream)));
            base.Dispose(disposing);
        }
    }
}
