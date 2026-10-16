using System.Net.WebSockets;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtyFrameReaderTests
{
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
