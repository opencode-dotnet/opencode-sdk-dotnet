using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Streaming.Support;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

using static OpenCode.Sdk.Tests.Streaming.Support.ServerSentEventReaderData;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventReaderLifecycleTests
{
    private const int MultiReadPayloadLength = 9000;

    [Test]
    public async Task ReadAsync_Should_Request_Exactly_8192_Bytes_On_Every_Short_Read()
    {
        var payload = new string('x', MultiReadPayloadLength);
        var body = Encoding.UTF8.GetBytes(WireBodyData.Frames(payload));
        var lengths = new List<int>();
        var position = 0;
        using var stream = new ControlledReadStream((buffer, _) =>
        {
            lengths.Add(buffer.Length);
            var count = Math.Min(1024, body.Length - position);
            body.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return Task.FromResult(count);
        });

        var frames = await new ServerSentEventReaderScenario().ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([payload], StringComparer.Ordinal, CollectionOrdering.Matching);
        // 9,008 body bytes take nine short reads, then one read reports EOF.
        await Assert.That(lengths).IsEquivalentTo(Enumerable.Repeat(8192, 10), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Null_Stream_Before_Enumeration()
    {
        var reader = new ServerSentEventReader();

        var exception = Assert.Throws<ArgumentNullException>(
            () => _ = reader.ReadAsync(null!, CancellationToken.None));

        await Assert.That(exception.ParamName).IsEqualTo("stream");
    }

    [Test]
    public async Task ReadAsync_Should_Abandon_A_Read_That_Blocks_After_Cancellation()
    {
        using var stream = new BlockingStream(Encoding.UTF8.GetBytes(FirstFrame));
        using var cancellation = new CancellationTokenSource();
        var reader = new ServerSentEventReader();
        var frames = new List<ServerSentEvent>();

        _ = await Assert.That(async () =>
        {
            await foreach (var frame in reader.ReadAsync(stream, cancellation.Token))
            {
                frames.Add(TestFrames.Copy(frame));
                await cancellation.CancelOnWorkerAsync();
            }
        }).Throws<OperationCanceledException>();

        await Assert.That(TestFrames.ReadText(frames.Single().Data)).IsEqualTo(FirstPayload);
    }

    [Test]
    public async Task ReadAsync_Should_Honor_Cancellation()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(FirstFrame);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert
            .That(async () => _ = await scenario.ReadAllAsync(stream, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ReadAsync_Should_Not_Yield_Another_Buffered_Frame_After_Cancellation()
    {
        using var stream = ChunkedStream.Of("data: first\n\ndata: second\n\n");
        using var cancellation = new CancellationTokenSource();
        await using var frames = new ServerSentEventReader()
            .ReadAsync(stream, cancellation.Token)
            .GetAsyncEnumerator(CancellationToken.None);
        _ = await frames.MoveNextAsync();
        var first = TestFrames.ReadText(frames.Current.Data);
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert
            .That(async () => _ = await frames.MoveNextAsync())
            .Throws<OperationCanceledException>();

        await Assert.That(first).IsEqualTo(FirstPayload);
    }

    [Test]
    public async Task ReadAsync_Should_Reuse_Data_Memory_On_The_Next_Move()
    {
        using var stream = ChunkedStream.Of(WireBodyData.Frames(FirstPayload, "other"));
        await using var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await frames.MoveNextAsync()).IsTrue();
        var held = frames.Current.Data;
        await Assert.That(TestFrames.ReadText(held)).IsEqualTo(FirstPayload);

        await Assert.That(await frames.MoveNextAsync()).IsTrue();

        await Assert.That(TestFrames.ReadText(frames.Current.Data)).IsEqualTo("other");
        await Assert.That(TestFrames.ReadText(held)).IsEqualTo("other");
    }

    [Test]
    public async Task ReadAsync_Should_Reset_A_Reused_Reader_After_Cancellation_Inside_A_Line()
    {
        const string prefix = "event: stale\ndata: old\ndata: par";
        using var interrupted = new GatedStream(Encoding.UTF8.GetBytes(prefix + prefix), chunkCount: 2);
        using var cancellation = new CancellationTokenSource();
        var reader = new ServerSentEventReader();
        await using (var frames = reader.ReadAsync(interrupted, cancellation.Token).GetAsyncEnumerator())
        {
            var move = ReadCancellationAsync(frames);
            try
            {
                _ = await interrupted.WaitForReadAsync(HangGuard.Bound);
                interrupted.Release();
                _ = await interrupted.WaitForReadAsync(HangGuard.Bound);
            }
            finally
            {
                await cancellation.CancelOnWorkerAsync();
                var exception = await move.WaitAsync(HangGuard.Bound);
                await Assert.That(exception.CancellationToken).IsEqualTo(cancellation.Token);
            }
        }

        using var fresh = ChunkedStream.Of("\uFEFFdata: new\n\ndata: own\n\n");
        using var freshCancellation = new CancellationTokenSource();
        var actual = new List<ServerSentEvent>();
        await foreach (var frame in reader.ReadAsync(fresh, freshCancellation.Token))
        {
            actual.Add(TestFrames.Copy(frame));
        }

        await Assert.That(actual.Select(frame => frame.Name)).IsEquivalentTo(
            [ServerSentEvent.DefaultName, ServerSentEvent.DefaultName], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(actual.Select(frame => TestFrames.ReadText(frame.Data))).IsEquivalentTo(
            ["new", "own"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Constructor_Should_Refuse_A_Nonpositive_Ceiling(int ceiling)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ServerSentEventReader(ceiling));

        await Assert.That(exception.ParamName).IsEqualTo("maxFrameCharacters");
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_The_Callers_Cancellation_During_The_Second_Read()
    {
        using var stream = new GatedStream(Encoding.UTF8.GetBytes("data: a\n\ndata: b\n\n"), chunkCount: 2);
        using var caller = new CancellationTokenSource();
        await using var frames = new ServerSentEventReader().ReadAsync(stream, caller.Token).GetAsyncEnumerator();
        stream.Release();
        await Assert.That(await frames.MoveNextAsync()).IsTrue();
        await Assert.That(TestFrames.ReadText(frames.Current.Data)).IsEqualTo(APayload);
        _ = await stream.WaitForReadAsync(HangGuard.Bound);
        var cancelled = ReadCancellationAsync(frames);
        try
        {
            _ = await stream.WaitForReadAsync(HangGuard.Bound);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            var exception = await cancelled.WaitAsync(HangGuard.Bound);
            await Assert.That(exception.CancellationToken).IsEqualTo(caller.Token);
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(MultiReadPayloadLength)]
    public async Task ReadAsync_Should_Dispose_After_A_Yield_With_Growth_And_Unread_Buffered_Frames(int length)
    {
        var payload = new string('x', length);
        using var stream = ChunkedStream.Of(WireBodyData.Frames(payload, "buffered"), "remaining");
        await using (var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator())
        {
            await Assert.That(await frames.MoveNextAsync()).IsTrue();
            await Assert.That(frames.Current.Name).IsEqualTo(ServerSentEvent.DefaultName);
            await Assert.That(TestFrames.ReadText(frames.Current.Data)).IsEqualTo(payload);
        }

        await Assert.That(stream.Disposed).IsFalse();
        var buffer = new byte[9];
        await Assert.That(await stream.ReadAsync(buffer.AsMemory())).IsEqualTo(9);
        await Assert.That(Encoding.UTF8.GetString(buffer)).IsEqualTo("remaining");
    }

    [Test]
    public async Task ReadAsync_Should_Observe_Cancellation_After_A_Read_Returns_A_Frame()
    {
        using var caller = new CancellationTokenSource();
        var bytes = Encoding.UTF8.GetBytes(KeptFrame);
        using var stream = new ControlledReadStream(async (buffer, _) =>
        {
            bytes.CopyTo(buffer);
            await caller.CancelOnWorkerAsync();
            return bytes.Length;
        });

        await AssertCallerCancellationAsync(stream, caller);
    }

    [Test]
    public async Task ReadAsync_Should_End_Normally_When_A_Read_Cancels_And_Returns_Empty_Eof()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var caller = new CancellationTokenSource();
        using var stream = new ControlledReadStream(async (_, _) =>
        {
            await caller.CancelOnWorkerAsync();
            return 0;
        });

        var frames = await scenario.ReadAllAsync(stream, cancellationToken: caller.Token);

        await Assert.That(frames).IsEmpty();
        await Assert.That(caller.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Check_Cancellation_Before_Yielding_A_Trailing_Frame()
    {
        using var caller = new CancellationTokenSource();
        var bytes = Encoding.UTF8.GetBytes("data: kept\n");
        var reads = 0;
        using var stream = new ControlledReadStream(async (buffer, _) =>
        {
            if (reads++ is 0)
            {
                bytes.CopyTo(buffer);
                return bytes.Length;
            }

            await caller.CancelOnWorkerAsync();
            return 0;
        });

        await AssertCallerCancellationAsync(stream, caller);
    }

    [Test]
    [Arguments(UnfinishedData, false)]
    [Arguments("event: named\n" + KeptFrame, true)]
    public async Task ReadAsync_Should_Propagate_A_Read_Failure_After_The_Delivered_Prefix(string prefix, bool yields)
    {
        var bytes = Encoding.UTF8.GetBytes(prefix);
        var failure = new IOException("Controlled read failure.");
        var reads = 0;
        using var stream = new ControlledReadStream((buffer, _) =>
        {
            if (reads++ > 0)
            {
                throw failure;
            }

            bytes.CopyTo(buffer);
            return Task.FromResult(bytes.Length);
        });
        await using var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator();
        if (yields)
        {
            await Assert.That(await frames.MoveNextAsync()).IsTrue();
            await Assert.That(frames.Current.Name).IsEqualTo("named");
            await Assert.That(TestFrames.ReadText(frames.Current.Data)).IsEqualTo(KeptPayload);
        }

        var exception = await Assert.That(async () => _ = await frames.MoveNextAsync()).Throws<IOException>();

        await Assert.That(exception).IsSameReferenceAs(failure);
    }

    private static async Task AssertCallerCancellationAsync(Stream stream, CancellationTokenSource caller)
    {
        // S8949 requires the in-scope token; repeating ReadAsync's token leaves cancellation semantics unchanged.
        await using var frames = new ServerSentEventReader().ReadAsync(stream, caller.Token).GetAsyncEnumerator(caller.Token);

        var exception = await ReadCancellationAsync(frames);

        await Assert.That(exception.CancellationToken).IsEqualTo(caller.Token);
    }

    private static async Task<OperationCanceledException> ReadCancellationAsync(IAsyncEnumerator<ServerSentEvent> frames)
    {
        var exception = await Assert.That(async () => _ = await frames.MoveNextAsync()).Throws<OperationCanceledException>();
        // The assertion guarantees an exception rather than a yielded frame.
        return exception!;
    }
}
