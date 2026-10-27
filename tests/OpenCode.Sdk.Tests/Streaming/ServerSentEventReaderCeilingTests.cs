using System.Text;
using OpenCode.Sdk.Tests.Streaming.Support;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

using static OpenCode.Sdk.Tests.Streaming.Support.ServerSentEventReaderData;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventReaderCeilingTests
{
    [Test]
    public async Task ReadAsync_Should_Refuse_A_Frame_Beyond_The_Size_Limit()
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 16);
        using var stream = ChunkedStream.Of($"data: {new string('x', 64)}\n\n");

        _ = await Assert
            .That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    // SDK frame-ceiling policy: count line characters, not their terminators.
    [Arguments(AFrame, 7, new[] { APayload })]
    // SDK frame-ceiling policy: comment characters count too.
    [Arguments(":x\ndata: a\n\n", 9, new[] { APayload })]
    // SDK frame-ceiling policy: the newline joining data values also counts.
    [Arguments("data: a\ndata: b\n\n", 15, new[] { JoinedAB })]
    // Frame ceiling's unit (SDK policy): é is one UTF-16 unit, not its two UTF-8 bytes.
    [Arguments("data: é\n\n", 7, new[] { "é" })]
    // Frame ceiling's unit (SDK policy): € is one UTF-16 unit, not its three UTF-8 bytes.
    [Arguments("data: €\n\n", 7, new[] { EuroCharacter })]
    // Frame ceiling's unit (SDK policy): 😀 is two UTF-16 units, not one scalar or four bytes.
    [Arguments("data: 😀\n\n", 8, new[] { EmojiCharacter })]
    [Arguments("event: x\ndata: a\n\n", 15, new[] { APayload })]
    [Arguments("unknown: x\ndata: a\n\n", 17, new[] { APayload })]
    [Arguments("event: first\nevent: x\ndata: a\n\n", 27, new[] { APayload })]
    [Arguments(LeadingBomAFrame, 7, new[] { APayload })]
    [Arguments("data: \uFEFFa\n\n", 8, new[] { "\uFEFFa" })]
    [Arguments("\uFEFF\uFEFFdata: ignored\ndata: a\n\n", 21, new[] { APayload })]
    [Arguments("data: a\r\r", 7, new[] { APayload })]
    [Arguments("data: a\r\n\r\n", 7, new[] { APayload })]
    public async Task ReadAsync_Should_Accept_At_The_Character_Ceiling_And_Refuse_One_Unit_Below(
        string body, int ceiling, string[] expected)
    {
        var scenario = new ServerSentEventReaderScenario(ceiling);
        await scenario.AssertBoundaryAsync([Encoding.UTF8.GetBytes(body)], expected);
    }

    [Test]
    [Arguments("\uFEFEdata: x\n\ndata: a\n\n", 8, 0)]
    [Arguments("\uFEFEdata: x\n\ndata: a\n\n", 8, 2)]
    [Arguments("\uFEFEdata: ignored\ndata: a\n\n", 21, 2)]
    [Arguments("\uFFFDdata: ignored\ndata: a\n\n", 21, 2)]
    public async Task ReadAsync_Should_Charge_A_Valid_Near_Bom_Prefix_At_The_Ceiling(
        string body, int ceiling, int split)
    {
        var scenario = new ServerSentEventReaderScenario(ceiling);
        var bytes = Encoding.UTF8.GetBytes(body);
        byte[][] chunks = split is 0 ? [bytes] : [[.. bytes.Take(split)], [.. bytes.Skip(split)]];

        await scenario.AssertBoundaryAsync(chunks, [APayload]);
    }

    [Test]
    // SDK frame-ceiling policy: the count resets after a frame that yields data.
    [Arguments("data: a\n\ndata: a\n\n", new[] { APayload, APayload })]
    // SDK frame-ceiling policy: the count also resets after a frame with no data.
    [Arguments(":x\n\ndata: a\n\n", new[] { APayload })]
    public async Task ReadAsync_Should_Reset_The_Character_Ceiling_After_Every_Frame(string body, string[] expected)
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 7);
        using var stream = ChunkedStream.Of(body);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Unterminated_Line_Beyond_The_Character_Ceiling()
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 16);
        // DefaultMaxFrameCharacters's contract bounds pending frames, even before a line terminates.
        using var stream = ChunkedStream.Of($"data: {new string('x', 64)}");

        _ = await Assert.That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>()
            .WithMessage(SizeLimitMessage, StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_Incomplete_Utf8_Before_Charging_The_Character_Ceiling()
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 6);
        using var stream = new MemoryStream([.. Encoding.UTF8.GetBytes(DataPrefix), 0xc5]);

        _ = await Assert.That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>()
            .WithMessage(MalformedUtf8Message, StringComparison.Ordinal);
    }

    [Test]
    [Arguments(APayload, 7)]
    [Arguments(TurkishCharacter, 7)]
    [Arguments(EuroCharacter, 7)]
    [Arguments(EmojiCharacter, 8)]
    public async Task ReadAsync_Should_Charge_The_Ceiling_When_A_Split_Character_Completes(string character, int ceiling)
    {
        var scenario = new ServerSentEventReaderScenario(ceiling);
        var bytes = Encoding.UTF8.GetBytes(character);
        for (var split = bytes.Length is 1 ? 0 : 1; split < bytes.Length; split++)
        {
            byte[][] chunks =
            [
                [.. Encoding.UTF8.GetBytes(DataPrefix), .. bytes.Take(split)],
                [.. bytes.Skip(split), .. Encoding.UTF8.GetBytes(FrameDelimiter)],
            ];
            await scenario.AssertBoundaryAsync(chunks, [character]);
        }
    }

    [Test]
    [Arguments(EuroCharacter)]
    [Arguments(EmojiCharacter)]
    public async Task ReadAsync_Should_Not_Charge_An_Unfinished_Scalar_At_The_Ceiling(string character)
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 6);
        var bytes = Encoding.UTF8.GetBytes(character);
        using var stream = ChunkedStream.OfBytes(Encoding.UTF8.GetBytes(DataPrefix), [.. bytes.Take(bytes.Length - 1)]);

        _ = await scenario.AssertRefusalAsync(stream, MalformedUtf8Message);
    }

    [Test]
    [Arguments(true, MalformedUtf8Message)]
    [Arguments(false, SizeLimitMessage)]
    public async Task ReadAsync_Should_Choose_Malformed_Or_Limit_By_The_Read_Boundary(bool sameRead, string message)
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 7);
        var text = Encoding.UTF8.GetBytes("data: xx");
        using var stream = sameRead ? ChunkedStream.OfBytes([.. text, 0xff]) : ChunkedStream.OfBytes(text, [0xff]);

        _ = await scenario.AssertRefusalAsync(stream, message);
    }

    [Test]
    [Arguments(new[] { "data: a", "b\n\n" }, 8, new[] { "ab" })]
    [Arguments(new[] { EventXLine, AFrame }, 15, new[] { APayload })]
    [Arguments(new[] { ACarriageReturnLine, "\n\r\n" }, 7, new[] { APayload })]
    [Arguments(new[] { ByteOrderMark, AFrame }, 7, new[] { APayload })]
    [Arguments(new[] { "data: a\n", "data: \uFEFFb\n\n" }, 16, new[] { "a\n\uFEFFb" })]
    public async Task ReadAsync_Should_Keep_The_Ceiling_Across_Split_Lines_And_Fields(
        string[] chunks, int ceiling, string[] expected)
    {
        var scenario = new ServerSentEventReaderScenario(ceiling);
        await scenario.AssertBoundaryAsync([.. chunks.Select(Encoding.UTF8.GetBytes)], expected);
    }

    [Test]
    [MatrixDataSource]
    public async Task ReadAsync_Should_Preserve_Exact_Line_And_Data_Growth_Boundaries(
        [Matrix(255, 256, 257, 511, 512, 513)] int length,
        [Matrix(false, true)] bool measuresData)
    {
        var scenario = new ServerSentEventReaderScenario();
        var payload = new string('x', measuresData ? length : length - DataPrefix.Length);
        using var stream = ChunkedStream.Of(WireBodyData.Frames(payload, "tail"));

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([payload, "tail"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Apply_The_Character_Ceiling_To_A_Multibyte_Unknown_Line()
    {
        var scenario = new ServerSentEventReaderScenario(maxFrameCharacters: 86);
        // The ceiling counts UTF-16 units even when the unknown line needs three bytes per unit.
        var unknownLine = new string('€', 86);
        await scenario.AssertBoundaryAsync([Encoding.UTF8.GetBytes($"{unknownLine}{FrameDelimiter}{KeptFrame}")], [KeptPayload]);
    }
}
