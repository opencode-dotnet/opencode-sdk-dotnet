using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// A raw-byte body is the caller's stream: the SDK reads it once from where it stands, never
/// disposes or rewinds it, declares a length only when the stream knows it, and bounds the upload
/// by progress the way the pipeline bounds a download.
/// </summary>
public sealed class CallerStreamContentTests
{
    /// <summary>Bounds a wait for an event that never comes; it decides no verdict.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Test]
    public async Task ExecuteAsync_Should_Send_The_Stream_From_Its_Position_Without_Disposing_Or_Rewinding_It()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        using var source = new MemoryStream([0x00, 0x01, 0x02, 0xFF, 0xFE, 0x0A]);
        source.Position = 2;

        _ = await pipeline.ExecuteAsync(
            HttpMethod.Post, "/api/experimental/fs/write?path=a.bin", source, new RecordingResponseAdapter(), options: null,
            CancellationToken.None);

        var recorded = handler.Requests.Single();
        await Assert.That(recorded.ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(recorded.ContentLength).IsEqualTo(4);
        await Assert.That(recorded.BodyBytes!).IsEquivalentTo(new byte[] { 0x02, 0xFF, 0xFE, 0x0A });
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(source.Position).IsEqualTo(6);
    }

    [Test]
    public async Task ExecuteAsync_Should_Send_A_Non_Seekable_Stream_Without_A_Declared_Length()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        using var source = new ForwardOnlyStream([0x41, 0x42, 0x43]);

        _ = await pipeline.ExecuteAsync(
            HttpMethod.Post, "/api/experimental/fs/write?path=a.txt", source, new RecordingResponseAdapter(), options: null,
            CancellationToken.None);

        var recorded = handler.Requests.Single();
        await Assert.That(recorded.ContentLength).IsNull();
        await Assert.That(recorded.BodyBytes!).IsEquivalentTo(new byte[] { 0x41, 0x42, 0x43 });
    }

    /// <summary>A redirect or a retry in a caller-supplied handler would need the bytes again.</summary>
    [Test]
    public async Task Content_Should_Refuse_A_Second_Send()
    {
        using var message = new PipelineMessage { Request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:4096/") };
        using var source = new MemoryStream([0x01, 0x02]);
        using var content = new CallerStreamContent(source, message);
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await content.CopyToAsync(first);

        _ = await Assert.That(async () => await content.CopyToAsync(second)).Throws<InvalidOperationException>();
        await Assert.That(first.ToArray()).IsEquivalentTo(new byte[] { 0x01, 0x02 });
    }

    /// <summary>
    /// Under progress semantics a slow upload that keeps moving survives: every chunk goes out just
    /// inside the window, and the upload as a whole outlives it several times over. The window runs
    /// on the test's clock, which moves only while the next read of the caller's stream waits, so no
    /// scheduling delay counts.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task ExecuteAsync_Should_Keep_A_Trickling_Upload_That_Outlives_The_Window(CancellationToken cancellationToken)
    {
        // Far beyond the hang guard, so only the test's clock can pass the window, never the host's.
        var window = TimeSpan.FromMinutes(2);
        var time = new ManualTimeProvider();
        var started = time.GetUtcNow();
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, networkTimeout: window, time: time);
        using var source = new GatedStream([.. Enumerable.Range(0, 12).Select(static value => (byte)value)], chunkCount: 12);

        var delivering = source.DeliverAsync(() => time.Advance(window - TimeSpan.FromTicks(1)), HangGuard);
        _ = await pipeline.ExecuteAsync(
            HttpMethod.Post, "/api/experimental/fs/write?path=a.txt", source, new RecordingResponseAdapter(), options: null,
            cancellationToken);
        await delivering;

        await Assert.That(handler.Requests.Single().BodyBytes!.Count).IsEqualTo(12);
        await Assert.That(time.GetUtcNow() - started).IsGreaterThan(TimeSpan.FromTicks(window.Ticks * 3));
    }

    [Test]
    [NotInParallel]
    public async Task ExecuteAsync_Should_Fail_A_Stalled_Upload_At_The_Progress_Window()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, networkTimeout: TimeSpan.FromMilliseconds(250));
        using var source = new TricklingStream(chunkCount: 2, gap: Timeout.InfiniteTimeSpan);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(
                HttpMethod.Post, "/api/experimental/fs/write?path=a.txt", source, new RecordingResponseAdapter(), options: null,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OpenCodeTransportException>();
    }

    /// <summary>A readable stream that can neither seek nor report a length.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    /// <summary>Yields one byte per read after waiting <c>gap</c>; the first byte comes at once.</summary>
    private sealed class TricklingStream(int chunkCount, TimeSpan gap) : Stream
    {
        private int _sent;

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
            ReadChunkAsync(buffer.AsMemory(offset, count), cancellationToken);

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(ReadChunkAsync(buffer, cancellationToken));
#endif

        [SlopwatchSuppress("SW004", "The delay is the subject under test: it paces a trickling upload against the progress window")]
        private async Task<int> ReadChunkAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_sent >= chunkCount)
            {
                return 0;
            }

            if (_sent > 0)
            {
                await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
            }

            buffer.Span[0] = (byte)_sent;
            _sent++;
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The trickling stream is read asynchronously only.");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
