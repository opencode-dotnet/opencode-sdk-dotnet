using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventReaderTests
{
    [Test]
    public async Task ReadAsync_Should_Yield_One_Payload_Per_Frame()
    {
        using var stream = ChunkedStream.Of("data: {\"a\":1}\n\ndata: {\"a\":2}\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(["{\"a\":1}", "{\"a\":2}"], StringComparer.Ordinal, CollectionOrdering.Matching);
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

        await Assert.That(frames).IsEquivalentTo(["first", "\uFEFFsecond"], StringComparer.Ordinal, CollectionOrdering.Matching);
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

        await Assert.That(frames).IsEquivalentTo(["{\"a\":1}", "{\"a\":2}"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_The_Next_Frame_After_A_Data_Line_Longer_Than_The_Read_Buffer()
    {
        var first = new string('x', 9000);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(WireBodyData.Frames(first, "next")));

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([first, "next"], StringComparer.Ordinal, CollectionOrdering.Matching);
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

        await Assert.That(frames).IsEquivalentTo(["first", "second"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Normalize_A_Carriage_Return_Split_Across_Chunks()
    {
        using var stream = ChunkedStream.Of("data: first\r", "\n\r\ndata: second\r\n\r\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(["first", "second"], StringComparer.Ordinal, CollectionOrdering.Matching);
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

        await Assert.That(frames).IsEquivalentTo([" padded", "tight"], StringComparer.Ordinal, CollectionOrdering.Matching);
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

    [Test]
    // SDK policy: strict UTF-8 applies to comments as well as data.
    [Arguments(": ", new byte[] { 0xff })]
    // SDK policy: ignored fields still require valid UTF-8.
    [Arguments("id: ", new byte[] { 0xff })]
    // SDK policy: event names also require valid UTF-8.
    [Arguments("event: ", new byte[] { 0xff })]
    // SDK strict UTF-8 policy: overlong encodings are malformed.
    [Arguments("data: ", new byte[] { 0xc0, 0xaf })]
    // SDK strict UTF-8 policy: surrogate code points are malformed.
    [Arguments("data: ", new byte[] { 0xed, 0xa0, 0x80 })]
    public async Task ReadAsync_Should_Refuse_Malformed_Utf8_In_A_Line(string prefix, byte[] invalidBytes)
    {
        using var stream = new MemoryStream([.. Encoding.UTF8.GetBytes(prefix), .. invalidBytes, (byte)'\n', (byte)'\n']);

        _ = await Assert.That(async () => _ = await ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>()
            .WithMessage("The opencode event stream contains malformed UTF-8.", StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_A_Leading_Byte_Order_Mark_Split_Into_Single_Bytes()
    {
        // WHATWG HTML, Server-sent events, Parsing an event stream: ignore one leading U+FEFF.
        using var stream = ChunkedStream.OfBytes("\uFEFFdata: a\n\n", chunkSize: 1);

        var frames = await ReadEventsAsync(stream);

        await Assert.That(frames.Single().Name).IsEqualTo(ServerSentEvent.DefaultName);
        await Assert.That(frames.Single().Data).IsEqualTo("a");
    }

    [Test]
    public async Task ReadAsync_Should_Yield_Nothing_For_A_Body_Containing_Only_A_Byte_Order_Mark()
    {
        // WHATWG HTML, Server-sent events, Parsing an event stream: ignore one leading U+FEFF.
        using var stream = ChunkedStream.Of("\uFEFF");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Not_Skip_A_Byte_Order_Mark_At_The_Start_Of_A_Later_Read()
    {
        // WHATWG HTML, Server-sent events, Parsing an event stream: only one leading U+FEFF is ignored.
        using var stream = ChunkedStream.Of("data: a\n\n", "\uFEFFdata: b\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(["a"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    // WHATWG HTML, Server-sent events, Parsing an event stream: CR alone ends a line.
    [Arguments("data: a\rdata: b\r\r", new[] { "a\nb" })]
    // WHATWG HTML, Server-sent events, Parsing an event stream: LF then CR ends two lines.
    [Arguments("data: a\n\rdata: b\n\n", new[] { "a", "b" })]
    public async Task ReadAsync_Should_Treat_Carriage_Return_As_A_Line_Terminator(string body, string[] expected)
    {
        using var stream = ChunkedStream.Of(body);

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    // WHATWG HTML, Server-sent events, Parsing an event stream: CRLF is one terminator, even across reads.
    [Arguments(new[] { "data: a\r", "\ndata: b\n\n" }, new[] { "a\nb" })]
    // WHATWG HTML, Server-sent events, Parsing an event stream: CR alone terminates; the next byte is retained.
    [Arguments(new[] { "data: a\r", "data: b\n\n" }, new[] { "a\nb" })]
    public async Task ReadAsync_Should_Pair_Only_A_Line_Feed_After_A_Carriage_Return_Across_Reads(
        string[] chunks, string[] expected)
    {
        using var stream = ChunkedStream.Of(chunks);

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Flush_A_Trailing_Frame_Ending_In_A_Carriage_Return()
    {
        // SDK end-of-body policy: a terminated data line is whole even without a closing blank line.
        // Unlike WHATWG dispatch, this contract flushes the trailing frame.
        using var stream = ChunkedStream.Of("data: a\r");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(["a"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_A_Data_Field_With_The_Wrong_Case()
    {
        // WHATWG HTML, Server-sent events, Interpreting an event stream: field names are case-sensitive;
        // a frame with no data dispatches nothing.
        using var stream = ChunkedStream.Of("Data: x\n\n");

        var frames = await ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    // WHATWG HTML, Server-sent events, Interpreting an event stream: the last event field sets the type.
    [Arguments("event: first\nevent: second\ndata: a\n\n", "second")]
    // WHATWG HTML, Server-sent events, Interpreting an event stream: an empty event type means message.
    [Arguments("event:\ndata: a\n\n", ServerSentEvent.DefaultName)]
    // WHATWG HTML, Server-sent events, Interpreting an event stream: an empty event value resets the type.
    [Arguments("event: first\nevent:\ndata: a\n\n", ServerSentEvent.DefaultName)]
    public async Task ReadAsync_Should_Name_A_Frame_From_The_Last_Event_Value(string body, string expectedName)
    {
        using var stream = ChunkedStream.Of(body);

        var frames = await ReadEventsAsync(stream);

        await Assert.That(frames.Single().Name).IsEqualTo(expectedName);
        await Assert.That(frames.Single().Data).IsEqualTo("a");
    }

    [Test]
    // SDK frame-ceiling policy: count line characters, not their terminators.
    [Arguments("data: a\n\n", 7, new[] { "a" })]
    // SDK frame-ceiling policy: comment characters count too.
    [Arguments(":x\ndata: a\n\n", 9, new[] { "a" })]
    // SDK frame-ceiling policy: the newline joining data values also counts.
    [Arguments("data: a\ndata: b\n\n", 15, new[] { "a\nb" })]
    // Frame ceiling's unit (SDK policy): é is one UTF-16 unit, not its two UTF-8 bytes.
    [Arguments("data: é\n\n", 7, new[] { "é" })]
    // Frame ceiling's unit (SDK policy): € is one UTF-16 unit, not its three UTF-8 bytes.
    [Arguments("data: €\n\n", 7, new[] { "€" })]
    // Frame ceiling's unit (SDK policy): 😀 is two UTF-16 units, not one scalar or four bytes.
    [Arguments("data: 😀\n\n", 8, new[] { "😀" })]
    public async Task ReadAsync_Should_Accept_At_The_Character_Ceiling_And_Refuse_One_Unit_Below(
        string body, int ceiling, string[] expected)
    {
        using var stream = ChunkedStream.Of(body);

        var frames = await ReadAllAsync(stream, ceiling);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);

        using var refusedStream = ChunkedStream.Of(body);

        _ = await Assert.That(async () => _ = await ReadAllAsync(refusedStream, ceiling - 1))
            .Throws<OpenCodeTransportException>()
            .WithMessage("The opencode event stream produced a frame beyond the size this client accepts.", StringComparison.Ordinal);
    }

    [Test]
    // SDK frame-ceiling policy: the count resets after a frame that yields data.
    [Arguments("data: a\n\ndata: a\n\n", new[] { "a", "a" })]
    // SDK frame-ceiling policy: the count also resets after a frame with no data.
    [Arguments(":x\n\ndata: a\n\n", new[] { "a" })]
    public async Task ReadAsync_Should_Reset_The_Character_Ceiling_After_Every_Frame(string body, string[] expected)
    {
        using var stream = ChunkedStream.Of(body);

        var frames = await ReadAllAsync(stream, maxFrameCharacters: 7);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Unterminated_Line_Beyond_The_Character_Ceiling()
    {
        // DefaultMaxFrameCharacters's contract bounds pending frames, even before a line terminates.
        using var stream = ChunkedStream.Of($"data: {new string('x', 64)}");

        _ = await Assert.That(async () => _ = await ReadAllAsync(stream, maxFrameCharacters: 16))
            .Throws<OpenCodeTransportException>()
            .WithMessage("The opencode event stream produced a frame beyond the size this client accepts.", StringComparison.Ordinal);
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
