using System.Net.WebSockets;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Ptys.Support;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtyFrameReaderTests
{
    [Test]
    public async Task Read_Should_Copy_Output_Before_The_Receive_Buffer_Is_Reused()
    {
        var payload = Encoding.UTF8.GetBytes("received é€😀");
        var frame = ReadOutput(payload);

        payload.AsSpan().Fill((byte)'x');

        await Assert.That(frame.Text).IsEqualTo("received é€😀");
    }

    [Test]
    public async Task Read_Should_Keep_Only_The_Reported_Output_Count()
    {
        var payload = Encoding.UTF8.GetBytes("firsttrailing");

        var frame = (PtyOutputFrame)PtyFrameReader.Read(WebSocketMessageType.Text, payload, count: 5);

        await Assert.That(frame.Text).IsEqualTo("first");
    }

    [Test]
    [Arguments(WebSocketMessageType.Text)]
    [Arguments(WebSocketMessageType.Binary)]
    public async Task Read_Should_Decode_An_Empty_Output_Message(WebSocketMessageType messageType)
    {
        var frame = (PtyOutputFrame)PtyFrameReader.Read(messageType, [], count: 0);

        await Assert.That(frame.Text).IsEqualTo(string.Empty);
    }

    public static IEnumerable<Func<byte[]>> OutputBytes() =>
    [
        static () => Encoding.UTF8.GetBytes("ASCII"),
        static () => [0xC3, 0xA9],
        static () => [0xE2, 0x82, 0xAC],
        static () => [0xF0, 0x9F, 0x98, 0x80],
        static () => [0xFF],
        static () => [0xE2, 0x82],
        static () => [0xED, 0xA0, 0x80],
        // Split pairs: € = [0xE2, 0x82] + [0xAC]; 😀 = [0xF0, 0x9F] + [0x98, 0x80].
        static () => [0xAC],
        static () => [0xF0, 0x9F],
        static () => [0x98, 0x80],
    ];

    /// <summary>Encoding.UTF8 pins "identical to today's decoder"; literal replacements differ between net472 and .NET.</summary>
    [Test]
    [MethodDataSource(nameof(OutputBytes))]
    public async Task Read_Should_Preserve_Replacement_Decoding_For_Text_Output(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var frame = ReadOutput(payload);

        await Assert.That(frame.Text).IsEqualTo(Encoding.UTF8.GetString(payload));
    }

    /// <summary>Encoding.UTF8 pins "identical to today's decoder"; literal replacements differ between net472 and .NET.</summary>
    [Test]
    [MethodDataSource(nameof(OutputBytes))]
    public async Task Read_Should_Preserve_Replacement_Decoding_For_Non_Control_Binary_Output(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var frame = (PtyOutputFrame)PtyFrameReader.Read(WebSocketMessageType.Binary, payload, payload.Length);

        await Assert.That(frame.Text).IsEqualTo(Encoding.UTF8.GetString(payload));
    }

    private static PtyOutputFrame ReadOutput(byte[] payload) =>
        (PtyOutputFrame)PtyFrameReader.Read(WebSocketMessageType.Text, payload, payload.Length);

    [Test]
    public Task Read_Should_Refuse_A_Control_Document_Truncated_After_Its_Cursor() =>
        AssertRejectedControlAsync("cursor-truncated-after-value.json");

    [Test]
    [Arguments("cursor-trailing-garbage.json")]
    [Arguments("cursor-second-value.json")]
    public Task Read_Should_Refuse_Content_After_A_Control_Document(string fixture) =>
        AssertRejectedControlAsync(fixture);

    private static async Task AssertRejectedControlAsync(string fixture)
    {
        var payload = PtyFrameData.ControlFrame(new FixtureLoader().LoadJson($"Ptys.{fixture}"));
        PtyFrame? frame = null;

        var failure = await Assert.That(() => frame = PtyFrameReader.Read(
                WebSocketMessageType.Binary, payload, payload.Length))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("control frame");
        await Assert.That(frame).IsNull();
    }

    [Test]
    public async Task Read_Should_Accept_Trailing_Whitespace_And_Preserve_The_First_Cursor_Among_Nested_Unknown_Members()
    {
        // FixtureLoader trims line endings; restore explicit JSON whitespace for this boundary.
        var body = new FixtureLoader().LoadJson("Ptys.cursor-valid-whitespace.json") + "\t\r\n";
        await AssertCursorAsync(body);
    }

    [Test]
    public Task Read_Should_Read_The_Existing_Control_Frame_As_The_Exact_Cursor() =>
        AssertCursorAsync(PtyFrameData.CursorControlJson);

    private static async Task AssertCursorAsync(string body)
    {
        var payload = PtyFrameData.ControlFrame(body);

        var frame = PtyFrameReader.Read(WebSocketMessageType.Binary, payload, payload.Length);

        await Assert.That(frame).IsTypeOf<PtyCursorFrame>();
        await Assert.That(((PtyCursorFrame)frame).Cursor).IsEqualTo(PtyFrameData.CursorValue);
    }
}
