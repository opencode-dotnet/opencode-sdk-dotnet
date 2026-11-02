using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PooledJsonContentTests
{
    private const int BodyLength = 3;
    private static readonly byte[] ExpectedBytes = [0xff, 0xff, 0xff];

    [Test]
    public async Task CopyToAsync_Should_Send_Exact_Bytes_And_Return_Cleared_Storage_After_Completion()
    {
        byte[] returnedPrefix = [];
        using var content = CreateContent(buffer => returnedPrefix = buffer.AsSpan(0, BodyLength).ToArray(), out var pool);
        using var stream = new WriteStream();

        await content.CopyToAsync(stream);
        await Assert.That(stream.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(pool.OutstandingCount).IsEqualTo(1);
        content.Dispose();

        await AssertClearedStorageReturnedAsync(returnedPrefix, pool);
    }

    [Test]
    public async Task Dispose_Should_Return_Cleared_Storage_Without_A_Write()
    {
        byte[] returnedPrefix = [];
        using var content = CreateContent(buffer => returnedPrefix = buffer.AsSpan(0, BodyLength).ToArray(), out var pool);

        content.Dispose();

        await AssertClearedStorageReturnedAsync(returnedPrefix, pool);
    }

    [Test]
    public async Task Dispose_Should_Return_Storage_Only_Once()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);

        content.Dispose();
        content.Dispose();

        await Assert.That(pool.ReturnCount).IsEqualTo(1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Should_Abandon_Storage_When_A_Write_Is_Still_Reading()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var stream = new WriteStream(wait: true);
        var writing = content.CopyToAsync(stream);

        content.Dispose();
        var returnsDuringWrite = pool.ReturnCount;
        stream.Release();
        await writing;

        await Assert.That(stream.WasTouched).IsTrue();
        await Assert.That(returnsDuringWrite).IsEqualTo(0);
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
        await Assert.That(stream.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);

        content.Dispose();

        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Should_Abandon_Storage_After_A_Faulted_Write()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var stream = new WriteStream(fault: true);

        _ = await Assert.That(async () => await content.CopyToAsync(stream)).Throws<InvalidOperationException>();
        content.Dispose();

        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task CopyToAsync_Should_Preserve_The_Bytes_For_A_Retry_After_A_Fault()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var faulting = new WriteStream(fault: true);
        using var retry = new WriteStream();

        _ = await Assert.That(async () => await content.CopyToAsync(faulting)).Throws<InvalidOperationException>();
        await content.CopyToAsync(retry);
        content.Dispose();

        await Assert.That(retry.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Should_Abandon_Storage_When_The_Second_Overlapping_Write_Finishes_First()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var first = new WriteStream(wait: true);
        using var second = new WriteStream();
        var writing = content.CopyToAsync(first);
        try
        {
            await content.CopyToAsync(second);
        }
        finally
        {
            first.Release();
            await writing;
        }

        content.Dispose();

        await Assert.That(first.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(second.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Should_Abandon_Storage_When_The_First_Overlapping_Write_Finishes_First()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var first = new WriteStream(wait: true);
        using var second = new WriteStream(wait: true);
        var firstWrite = content.CopyToAsync(first);
        var secondWrite = content.CopyToAsync(second);
        int returnsDuringSecondWrite;
        try
        {
            first.Release();
            await firstWrite;
            content.Dispose();
            returnsDuringSecondWrite = pool.ReturnCount;
        }
        finally
        {
            first.Release();
            second.Release();
            await Task.WhenAll(firstWrite, secondWrite);
        }

        await Assert.That(returnsDuringSecondWrite).IsEqualTo(0);
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
        await Assert.That(first.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(second.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WriteAsync_Should_Refuse_An_Untouched_Second_Stream_After_Fault_And_Disposal()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var first = new WriteStream(fault: true);
        using var second = new WriteStream();

        _ = await Assert.That(async () => await content.WriteAsync(first, CancellationToken.None)).Throws<InvalidOperationException>();
        content.Dispose();
        _ = await Assert.That(async () => await content.WriteAsync(second, CancellationToken.None)).Throws<ObjectDisposedException>();

        await Assert.That(second.WasTouched).IsFalse();
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Should_Return_Storage_After_Two_Completed_Sequential_Writes()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var first = new WriteStream();
        using var second = new WriteStream();

        await content.CopyToAsync(first);
        await content.CopyToAsync(second);
        content.Dispose();

        await Assert.That(first.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(second.Bytes).IsEquivalentTo(ExpectedBytes, CollectionOrdering.Matching);
        await Assert.That(pool.ReturnCount).IsEqualTo(1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task WriteAsync_Should_Refuse_A_Write_After_Disposal()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var stream = new WriteStream();
        content.Dispose();

        _ = await Assert.That(async () => await content.WriteAsync(stream, CancellationToken.None)).Throws<ObjectDisposedException>();

        await Assert.That(stream.WasTouched).IsFalse();
        await Assert.That(pool.ReturnCount).IsEqualTo(1);
    }

    [Test]
    public async Task ContentLength_Should_Declare_Only_The_Written_Length()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);

        await Assert.That(content.Headers.ContentLength).IsEqualTo(BodyLength);
    }

#if NET
    [Test]
    public async Task CopyToAsync_Should_Abandon_Storage_When_The_Write_Observes_Cancellation()
    {
        var pool = new TrackingByteArrayPool();
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var stream = new WriteStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        _ = await Assert.That(async () => await content.CopyToAsync(stream, cancellation.Token)).Throws<OperationCanceledException>();
        content.Dispose();

        await Assert.That(stream.WasTouched).IsTrue();
        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }
#endif

    private static PooledJsonContent CreateContent(Action<byte[]> onReturn, out TrackingByteArrayPool pool)
    {
        pool = new TrackingByteArrayPool(onReturn: onReturn);
        return new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
    }

    private static async Task AssertClearedStorageReturnedAsync(byte[] returnedPrefix, TrackingByteArrayPool pool)
    {
        await Assert.That(returnedPrefix).IsEquivalentTo(new byte[BodyLength], CollectionOrdering.Matching);
        await Assert.That(pool.ReturnCount).IsEqualTo(1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    private sealed class WriteStream(bool fault = false, bool wait = false) : Stream
    {
        private TaskCompletionSource<bool>? _release;

        public bool WasTouched { get; private set; }

        public byte[] Bytes { get; private set; } = [];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Bytes.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _ = _release?.TrySetResult(true);

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteCoreAsync(buffer.AsMemory(offset, count), cancellationToken);

#if NET
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WriteCoreAsync(buffer, cancellationToken));
#endif

        private async Task WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            WasTouched = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (fault)
            {
                throw new InvalidOperationException("The test stream refused the write.");
            }

            if (wait)
            {
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _release = release;
                _ = await release.Task;
            }

            Bytes = buffer.ToArray();
        }
    }
}
