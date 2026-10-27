using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Streaming.Support;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

using static OpenCode.Sdk.Tests.Streaming.Support.ServerSentEventReaderData;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventReaderUtf8Tests
{
    [Test]
    public async Task ReadAsync_Should_Reassemble_A_Multi_Byte_Character_Split_Across_Chunks()
    {
        var scenario = new ServerSentEventReaderScenario();
        // The 'ş' encodes as two bytes; the split lands between them.
        using var stream = ChunkedStream.OfBytes("data: işler\n\n", chunkSize: 8);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("işler");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Invalid_Utf8_Byte()
    {
        var scenario = new ServerSentEventReaderScenario();
        var body = Encoding.UTF8.GetBytes("data: value\n\n");
        body[DataPrefix.Length] = 0xff;
        using var stream = new MemoryStream(body);

        var exception = await Assert
            .That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<DecoderFallbackException>();
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Incomplete_Utf8_Character_At_End_Of_Body()
    {
        var scenario = new ServerSentEventReaderScenario();
        var prefix = Encoding.UTF8.GetBytes(DataPrefix);
        using var stream = new MemoryStream([.. prefix, 0xc5]);

        var exception = await Assert
            .That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<DecoderFallbackException>();
    }

    [Test]
    // SDK policy: strict UTF-8 applies to comments as well as data.
    [Arguments(CommentPrefix, new byte[] { 0xff })]
    // SDK policy: ignored fields still require valid UTF-8.
    [Arguments("id: ", new byte[] { 0xff })]
    // SDK policy: event names also require valid UTF-8.
    [Arguments(EventPrefix, new byte[] { 0xff })]
    // SDK strict UTF-8 policy: overlong encodings are malformed.
    [Arguments(DataPrefix, new byte[] { 0xc0, 0xaf })]
    // SDK strict UTF-8 policy: surrogate code points are malformed.
    [Arguments(DataPrefix, new byte[] { 0xed, 0xa0, 0x80 })]
    public async Task ReadAsync_Should_Refuse_Malformed_Utf8_In_A_Line(string prefix, byte[] invalidBytes)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = new MemoryStream([.. Encoding.UTF8.GetBytes(prefix), .. invalidBytes, (byte)'\n', (byte)'\n']);

        _ = await Assert.That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_Unterminated_Lone_Continuation_Bytes()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = new MemoryStream([.. Encoding.UTF8.GetBytes(DataPrefix), .. Enumerable.Repeat((byte)0x80, 64)]);

        _ = await Assert.That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadAsync_Should_Validate_The_Whole_Read_Before_Delivering_A_Frame()
    {
        using var stream = new MemoryStream([.. Encoding.UTF8.GetBytes("event: surprise\ndata: {}\n\n: "), 0xff, (byte)'\n', (byte)'\n']);
        var delivered = new List<string>();

        _ = await Assert.That(async () =>
        {
            await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None))
            {
                delivered.Add(frame.Name);
            }
        }).Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);

        await Assert.That(delivered).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Report_Decoder_Diagnostics_From_The_Start_Of_The_Read()
    {
        var scenario = new ServerSentEventReaderScenario();
        byte[] body = [.. Encoding.UTF8.GetBytes($"data: {new string('x', 2000)}"), 0xff, .. Encoding.UTF8.GetBytes(FrameDelimiter)];
        using var stream = new MemoryStream(body);

        var exception = await Assert.That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);
        var fallback = await Assert.That(exception!.InnerException).IsTypeOf<DecoderFallbackException>();

        // The type assertion above guarantees the inner exception is present.
        await Assert.That(fallback!.Index).IsEqualTo(2006);
        await Assert.That(fallback.BytesUnknown).IsEquivalentTo(new byte[] { 0xff }, CollectionOrdering.Matching);
        await Assert.That(fallback.Message).IsEqualTo(ReplayDecoderFailure(body).Message);
    }

    [Test]
    [MatrixDataSource]
    public async Task ReadAsync_Should_Reassemble_Every_Inner_Utf8_Split_In_Each_Field(
        [Matrix(DataPrefix, EventPrefix, CommentPrefix, UnknownPrefix)] string prefix,
        [Matrix(TurkishCharacter, EuroCharacter, EmojiCharacter)] string character)
    {
        var scenario = new ServerSentEventReaderScenario();
        var bytes = Encoding.UTF8.GetBytes(character);
        for (var split = 1; split < bytes.Length; split++)
        {
            using var stream = ChunkedStream.OfBytes(
                [.. Encoding.UTF8.GetBytes(prefix), .. bytes.Take(split)],
                [.. bytes.Skip(split), .. Encoding.UTF8.GetBytes($"\n{KeptFrame}")]);

            await scenario.AssertSingleFrameAsync(stream, prefix is EventPrefix ? character : ServerSentEvent.DefaultName,
                prefix is DataPrefix ? $"{character}\n{KeptPayload}" : KeptPayload);
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task ReadAsync_Should_Refuse_Split_Malformed_Utf8_In_Each_Field(
        [Matrix(DataPrefix, EventPrefix, CommentPrefix, UnknownPrefix)] string prefix)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.OfBytes(
            [.. Encoding.UTF8.GetBytes(prefix), 0xe2],
            [0x28, .. Encoding.UTF8.GetBytes(FrameDelimiter)]);

        await scenario.AssertRefusalAsync(stream, MalformedUtf8Message);
    }

    [Test]
    [MatrixDataSource]
    public async Task ReadAsync_Should_Refuse_Every_Incomplete_Utf8_Prefix_At_Eof_In_Each_Field(
        [Matrix(DataPrefix, EventPrefix, CommentPrefix, UnknownPrefix)] string prefix,
        [Matrix(EuroCharacter, EmojiCharacter)] string character)
    {
        var scenario = new ServerSentEventReaderScenario();
        var bytes = Encoding.UTF8.GetBytes(character);
        for (var length = 1; length < bytes.Length; length++)
        {
            using var stream = ChunkedStream.OfBytes([.. Encoding.UTF8.GetBytes(prefix), .. bytes.Take(length)]);

            await scenario.AssertRefusalAsync(stream, MalformedUtf8Message);
        }
    }

    [Test]
    [Arguments(new byte[] { 0xff }, 6, new byte[] { 0xff })]
    [Arguments(new byte[] { 0xe2, 0x82 }, -2, new byte[] { 0xe2, 0x82 })]
    public async Task ReadAsync_Should_Preserve_Short_Read_And_Flush_Decoder_Diagnostics(
        byte[] suffix, int index, byte[] unknown)
    {
        var scenario = new ServerSentEventReaderScenario();
        byte[] body = [.. Encoding.UTF8.GetBytes(DataPrefix), .. suffix];
        using var stream = ChunkedStream.OfBytes(body);

        var exception = await scenario.AssertRefusalAsync(stream, MalformedUtf8Message);
        var fallback = await Assert.That(exception.InnerException).IsTypeOf<DecoderFallbackException>();

        // The type assertion guarantees that the fallback diagnostics are present.
        await Assert.That(fallback!.Index).IsEqualTo(index);
        await Assert.That(fallback.BytesUnknown).IsEquivalentTo(unknown, CollectionOrdering.Matching);
        await Assert.That(fallback.Message).IsEqualTo(ReplayDecoderFailure(body).Message);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadAsync_Should_Deliver_A_Complete_Frame_Before_A_Later_Read_Or_Eof_Is_Malformed(bool incomplete)
    {
        var first = Encoding.UTF8.GetBytes("event: surprise\ndata: {}\n\n");
        using var stream = incomplete
            ? ChunkedStream.OfBytes([.. first, .. Encoding.UTF8.GetBytes(CommentPrefix), 0xe2, 0x82])
            : ChunkedStream.OfBytes(first, [.. Encoding.UTF8.GetBytes(CommentPrefix), 0xff, .. Encoding.UTF8.GetBytes(FrameDelimiter)]);
        await using var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator();

        await Assert.That(await frames.MoveNextAsync()).IsTrue();
        await Assert.That(frames.Current.Name).IsEqualTo("surprise");
        await Assert.That(TestFrames.ReadText(frames.Current.Data)).IsEqualTo("{}");
        _ = await Assert.That(async () => _ = await frames.MoveNextAsync()).Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Malformed_Near_Bom_Across_Reads()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.OfBytes([0xef, 0xbb], [0x41, .. Encoding.UTF8.GetBytes(FrameDelimiter)]);

        _ = await scenario.AssertRefusalAsync(stream, MalformedUtf8Message);
    }

    [Test]
    public async Task ReadAsync_Should_Validate_Returned_Bytes_Before_Observing_Cancellation()
    {
        using var caller = new CancellationTokenSource();
        using var stream = new ControlledReadStream(async (buffer, _) =>
        {
            buffer.Span[0] = 0xff;
            await caller.CancelOnWorkerAsync();
            return 1;
        });
        await using var frames = new ServerSentEventReader().ReadAsync(stream, caller.Token).GetAsyncEnumerator();

        _ = await Assert.That(async () => _ = await frames.MoveNextAsync()).Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);
    }

    private static DecoderFallbackException ReplayDecoderFailure(params byte[][] chunks)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var decoder = encoding.GetDecoder();
        try
        {
            foreach (var chunk in chunks)
            {
                _ = decoder.GetChars(chunk, 0, chunk.Length, new char[encoding.GetMaxCharCount(chunk.Length)], 0, flush: false);
            }

            _ = decoder.GetChars([], 0, 0, new char[encoding.GetMaxCharCount(0)], 0, flush: true);
        }
        catch (DecoderFallbackException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The decoder oracle requires malformed UTF-8.");
    }
}
