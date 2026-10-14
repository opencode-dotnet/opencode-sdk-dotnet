using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventReaderTests
{
    [Test]
    public async Task ReadAsync_Should_Yield_One_Payload_Per_Frame()
    {
        using var stream = ChunkedStream.Of("data: {\"a\":1}\n\ndata: {\"a\":2}\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual(["{\"a\":1}", "{\"a\":2}"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Join_Multi_Line_Data_With_Newlines()
    {
        using var stream = ChunkedStream.Of("data: first\ndata: second\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_Every_Field_Except_Data_And_Event()
    {
        using var stream = ChunkedStream.Of(": heartbeat\nid: 42\nretry: 500\ndata: kept\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("kept");
    }

    [Test]
    public async Task ReadAsync_Should_Name_A_Frame_Without_An_Event_Field_Message()
    {
        using var stream = ChunkedStream.Of("data: kept\n\n");

        var frames = await ReadEventsAsync(stream);

        await Assert.That(frames.Single().Name).IsEqualTo(ServerSentEvent.DefaultName);
    }

    [Test]
    public async Task ReadAsync_Should_Carry_The_Name_Of_A_Named_Frame()
    {
        using var stream = ChunkedStream.Of($"event: {TestStreamAdapter.StreamFailureEventName}\ndata: cause\n\n");

        var frames = await ReadEventsAsync(stream);

        await Assert.That(frames.Single().Name).IsEqualTo(TestStreamAdapter.StreamFailureEventName);
        await Assert.That(frames.Single().Data).IsEqualTo("cause");
    }

    [Test]
    public async Task ReadAsync_Should_Not_Carry_An_Event_Name_Across_Frames()
    {
        using var stream = ChunkedStream.Of($"event: {TestStreamAdapter.StreamFailureEventName}\ndata: first\n\ndata: second\n\n");

        var frames = await ReadEventsAsync(stream);

        await Assert.That(frames[0].Name).IsEqualTo(TestStreamAdapter.StreamFailureEventName);
        await Assert.That(frames[1].Name).IsEqualTo(ServerSentEvent.DefaultName);
    }

    [Test]
    public async Task ReadAsync_Should_Join_Multi_Line_Data_Split_By_Crlf()
    {
        using var stream = ChunkedStream.Of("data: first\r\ndata: second\r\n\r\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("first\nsecond");
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_A_Leading_Byte_Order_Mark()
    {
        using var stream = ChunkedStream.Of("\uFEFFdata: first\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("first");
    }

    [Test]
    public async Task ReadAsync_Should_Keep_A_Byte_Order_Mark_Inside_A_Payload()
    {
        using var stream = ChunkedStream.Of("data: first\n\ndata: \uFEFFsecond\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual(["first", "\uFEFFsecond"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Body_That_Ends_Mid_Line()
    {
        using var stream = ChunkedStream.Of("data: {\"a\":1}\n\ndata: {\"a\"");

        _ = await Assert
            .That(async () => _ = await ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ReadAsync_Should_Read_An_Empty_Data_Value_As_A_Blank_Line()
    {
        using var stream = ChunkedStream.Of("data:\ndata: second\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("\nsecond");
    }

    [Test]
    public async Task ReadAsync_Should_Read_A_Field_Without_A_Colon_As_An_Empty_Value()
    {
        using var stream = ChunkedStream.Of("data\ndata: second\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("\nsecond");
    }

    [Test]
    public async Task ReadAsync_Should_Yield_Nothing_For_An_Empty_Body()
    {
        using var stream = ChunkedStream.Of(string.Empty);

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
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
        using var stream = new BlockingStream(Encoding.UTF8.GetBytes("data: first\n\n"));
        using var cancellation = new CancellationTokenSource();
        var reader = new ServerSentEventReader();
        var frames = new List<ServerSentEvent>();

        _ = await Assert.That(async () =>
        {
            await foreach (var frame in reader.ReadAsync(stream, cancellation.Token))
            {
                frames.Add(frame);
                await cancellation.CancelOnWorkerAsync();
            }
        }).Throws<OperationCanceledException>();

        await Assert.That(frames.Single().Data).IsEqualTo("first");
    }

    [Test]
    public async Task ReadAsync_Should_Skip_A_Frame_Carrying_No_Data()
    {
        using var stream = ChunkedStream.Of(": heartbeat\n\ndata: kept\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("kept");
    }

    [Test]
    public async Task ReadAsync_Should_Reassemble_A_Frame_Split_Across_Chunks()
    {
        using var stream = ChunkedStream.Of("data: {\"a", "\":1}\n", "\ndata: {\"a\":2}\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual(["{\"a\":1}", "{\"a\":2}"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_The_Next_Frame_After_A_Data_Line_Longer_Than_The_Read_Buffer()
    {
        var first = new string('x', 9000);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(WireBodyData.Frames(first, "next")));

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual([first, "next"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Reassemble_A_Multi_Byte_Character_Split_Across_Chunks()
    {
        // The 'ş' encodes as two bytes; the split lands between them.
        using var stream = ChunkedStream.OfBytes("data: işler\n\n", chunkSize: 8);

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("işler");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Invalid_Utf8_Byte()
    {
        var body = Encoding.UTF8.GetBytes("data: value\n\n");
        body["data: ".Length] = 0xff;
        using var stream = new MemoryStream(body);

        var exception = await Assert
            .That(async () => _ = await ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<DecoderFallbackException>();
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Incomplete_Utf8_Character_At_End_Of_Body()
    {
        var prefix = Encoding.UTF8.GetBytes("data: ");
        using var stream = new MemoryStream([.. prefix, 0xc5]);

        var exception = await Assert
            .That(async () => _ = await ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<DecoderFallbackException>();
    }

    [Test]
    public async Task ReadAsync_Should_Normalize_Crlf_Line_Endings()
    {
        using var stream = ChunkedStream.Of("data: first\r\n\r\ndata: second\r\n\r\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual(["first", "second"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Normalize_A_Carriage_Return_Split_Across_Chunks()
    {
        using var stream = ChunkedStream.Of("data: first\r", "\n\r\ndata: second\r\n\r\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual(["first", "second"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Flush_A_Trailing_Frame_Without_Its_Blank_Line()
    {
        using var stream = ChunkedStream.Of("data: last\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("last");
    }

    [Test]
    public async Task ReadAsync_Should_Strip_Exactly_One_Space_After_The_Field_Name()
    {
        using var stream = ChunkedStream.Of("data:  padded\n\ndata:tight\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames.SequenceEqual([" padded", "tight"], StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Frame_Beyond_The_Size_Limit()
    {
        using var stream = ChunkedStream.Of($"data: {new string('x', 64)}\n\n");

        _ = await Assert
            .That(async () => _ = await ReadAllAsync(stream, maxFrameCharacters: 16))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ReadAsync_Should_Honor_Cancellation()
    {
        using var stream = ChunkedStream.Of("data: first\n\n");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert
            .That(async () => _ = await ReadAllAsync(stream, cancellationToken: cancellation.Token))
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
        var first = frames.Current.Data;
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert
            .That(async () => _ = await frames.MoveNextAsync())
            .Throws<OperationCanceledException>();

        await Assert.That(first).IsEqualTo("first");
    }

    private static async Task<List<string>> ReadAllAsync(Stream stream,
        int maxFrameCharacters = ServerSentEventReader.DefaultMaxFrameCharacters,
        CancellationToken cancellationToken = default)
    {
        var frames = await ReadEventsAsync(stream, maxFrameCharacters, cancellationToken);

        return [.. frames.Select(static frame => frame.Data)];
    }

    private static async Task<List<ServerSentEvent>> ReadEventsAsync(Stream stream,
        int maxFrameCharacters = ServerSentEventReader.DefaultMaxFrameCharacters,
        CancellationToken cancellationToken = default)
    {
        var frames = new List<ServerSentEvent>();
        await foreach (var frame in new ServerSentEventReader(maxFrameCharacters).ReadAsync(stream, cancellationToken))
        {
            frames.Add(frame);
        }

        return frames;
    }
}
