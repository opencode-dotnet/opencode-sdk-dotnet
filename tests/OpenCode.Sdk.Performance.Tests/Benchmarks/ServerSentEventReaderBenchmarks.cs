using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The stream frame reader in isolation: a run of event frames walked through the same
/// decoded-span line scanner and state machine the SDK reads a live body with. A reader pays for
/// decoded-character scanning/copying, line handling, and frame dispatch; one payload size cannot
/// separate them. Large frames amortize dispatch while retaining scan/copy cost, small frames emphasize
/// line and frame work, and the multi-line form measures the data-line join against the one-line wire.
/// The empty body is the per-stream fixed cost (the reader's buffers, its enumerator and the
/// harness stream), so a single-frame case minus it is one frame's cost.
/// The buffered and chunked rungs complete every read synchronously and measure CPU work alone. The
/// suspending rungs read a body whose every read suspends, as a socket's does: the drain adds the
/// reader's resume cost, the early break measures leaving a stream after its first frame (the
/// enumerator's disposal), and the pending-read rung measures cancelling a read in flight.
/// </summary>
[MemoryDiagnoser]
public class ServerSentEventReaderBenchmarks
{
    private const int SmallFrameCount = 1024;
    private const int LargeFrameCount = 64;
    private const int LinesPerMultiLineFrame = 4;
    private const int SocketChunkBytes = 1460;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var large = BenchmarkFixtures.DeepAssistantMessage();
        var small = BenchmarkFixtures.SessionIdleBody();
        yield return new WireFixture("empty", [], items: 0, payloadBytesPerItem: 0);
        yield return Frames("large-x1", large, 1);
        yield return Frames("large-x64", large, LargeFrameCount);
        yield return Frames("small-x1", small, 1);
        yield return Frames("small-x1024", small, SmallFrameCount);
        yield return new WireFixture("large-x64-multiline", BenchmarkFixtures.MultiLineEventStream(large, LargeFrameCount, LinesPerMultiLineFrame),
            LargeFrameCount, payloadBytesPerItem: large.Length);
    }

    public static IEnumerable<WireFixture> ChunkedFixtures()
    {
        yield return Frames("large-x64", BenchmarkFixtures.DeepAssistantMessage(), LargeFrameCount);
        yield return Frames("small-x1024", BenchmarkFixtures.SessionIdleBody(), SmallFrameCount);
    }

    /// <summary>
    /// Proves each rung reads what it claims: every fixture frames into its count, the suspending
    /// reads really suspended (a stream that completed synchronously would measure the CPU rung
    /// twice), the early break left the body unread, and the pending read was in flight when it was
    /// cancelled.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        foreach (var fixture in Fixtures())
        {
            if (await ReadFramesAsync(fixture).ConfigureAwait(false) != fixture.Items)
            {
                throw new InvalidOperationException($"Fixture '{fixture.Name}' did not frame into the expected event count.");
            }
        }

        foreach (var fixture in ChunkedFixtures())
        {
            if (await ReadFramesChunkedAsync(fixture).ConfigureAwait(false) != fixture.Items)
            {
                throw new InvalidOperationException($"Chunked fixture '{fixture.Name}' did not frame into the expected event count.");
            }

            using (var stream = new ChunkedReadStream(fixture.Bytes, SocketChunkBytes, suspend: true))
            {
                if (await CountFramesAsync(stream).ConfigureAwait(false) != fixture.Items
                    || stream.SuspendedReads < stream.MinimumReads)
                {
                    throw new InvalidOperationException($"Suspending fixture '{fixture.Name}' did not frame its count over suspended reads.");
                }
            }

            using (var stream = new ChunkedReadStream(fixture.Bytes, SocketChunkBytes, suspend: true))
            {
                if (await ReadFirstFrameAsync(stream).ConfigureAwait(false) != 1 || stream.Position >= stream.Length || stream.SuspendedReads < 1)
                {
                    throw new InvalidOperationException($"Early break over '{fixture.Name}' did not stop after one frame of a suspending body.");
                }
            }
        }

        using var pending = new PendingReadStream();
        if (!await CancelPendingReadAsync(pending).ConfigureAwait(false) || pending.PendingReads != 1)
        {
            throw new InvalidOperationException("The pending-read rung did not cancel exactly one read in flight.");
        }
    }

    /// <summary>Frames one complete body, counting dispatched events.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(Fixtures))]
    public async Task<int> ReadFramesAsync(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        using var stream = new MemoryStream(fixture.Bytes);
        return await CountFramesAsync(stream).ConfigureAwait(false);
    }

    /// <summary>Frames one complete body delivered in socket-sized reads, so cross-read assembly is measured.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(ChunkedFixtures))]
    public async Task<int> ReadFramesChunkedAsync(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        using var stream = new ChunkedReadStream(fixture.Bytes, SocketChunkBytes);
        return await CountFramesAsync(stream).ConfigureAwait(false);
    }

    /// <summary>The chunked rung over reads that each suspend: the same CPU work plus the reader's resumes.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(ChunkedFixtures))]
    public async Task<int> ReadFramesSuspendingAsync(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        using var stream = new ChunkedReadStream(fixture.Bytes, SocketChunkBytes, suspend: true);
        return await CountFramesAsync(stream).ConfigureAwait(false);
    }

    /// <summary>One frame of a suspending body, then leaving: the reader's enumerator disposes mid-stream.</summary>
    [Benchmark]
    [ArgumentsSource(nameof(ChunkedFixtures))]
    public async Task<int> ReadFirstFrameSuspendingAsync(WireFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        using var stream = new ChunkedReadStream(fixture.Bytes, SocketChunkBytes, suspend: true);
        return await ReadFirstFrameAsync(stream).ConfigureAwait(false);
    }

    /// <summary>Cancelling a read that is in flight: the token's registration, the cancelled read, and the reader's unwinding.</summary>
    [Benchmark]
    public async Task<bool> CancelPendingReadAsync()
    {
        using var stream = new PendingReadStream();
        return await CancelPendingReadAsync(stream).ConfigureAwait(false);
    }

    private static async Task<int> CountFramesAsync(Stream stream)
    {
        var frames = 0;
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            _ = frame;
            frames++;
        }

        return frames;
    }

    /// <summary>Takes one frame and leaves, as a consumer's <c>break</c> does: the enumerator disposes mid-stream.</summary>
    private static async Task<int> ReadFirstFrameAsync(Stream stream)
    {
        var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            return await frames.MoveNextAsync().ConfigureAwait(false) ? 1 : 0;
        }
        finally
        {
            await frames.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Starts a read, cancels it while it is pending, and reports whether the reader ended with the cancellation.</summary>
    private static async Task<bool> CancelPendingReadAsync(PendingReadStream stream)
    {
        using var cancellation = new CancellationTokenSource();
        var frames = new ServerSentEventReader().ReadAsync(stream, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        try
        {
            var next = frames.MoveNextAsync();
            if (next.IsCompleted)
            {
                return false;
            }

            await cancellation.CancelOnWorkerAsync().ConfigureAwait(false);
            _ = await next.ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        finally
        {
            await frames.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static WireFixture Frames(string name, byte[] payload, int count) =>
        new(name, BenchmarkFixtures.EventStream(payload, count), count, payloadBytesPerItem: payload.Length);
}
