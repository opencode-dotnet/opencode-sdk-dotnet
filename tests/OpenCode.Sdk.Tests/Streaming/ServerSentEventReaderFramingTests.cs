using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Streaming.Support;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

using static OpenCode.Sdk.Tests.Streaming.Support.ServerSentEventReaderData;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventReaderFramingTests
{
    [Test]
    public async Task ReadAsync_Should_Yield_One_Payload_Per_Frame()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: {\"a\":1}\n\ndata: {\"a\":2}\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([JsonFirstPayload, JsonSecondPayload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Join_Multi_Line_Data_With_Newlines()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: first\ndata: second\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(JoinedFirstAndSecond);
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_Every_Field_Except_Data_And_Event()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(": heartbeat\nid: 42\nretry: 500\n" + KeptFrame);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(KeptPayload);
    }

    [Test]
    public async Task ReadAsync_Should_Name_A_Frame_Without_An_Event_Field_Message()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(KeptFrame);

        var frames = await scenario.ReadEventsAsync(stream);

        await Assert.That(frames.Single().Name).IsEqualTo(ServerSentEvent.DefaultName);
    }

    [Test]
    public async Task ReadAsync_Should_Carry_The_Name_Of_A_Named_Frame()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of($"event: {TestStreamAdapter.StreamFailureEventName}\ndata: cause\n\n");

        await scenario.AssertSingleFrameAsync(stream, TestStreamAdapter.StreamFailureEventName, "cause");
    }

    [Test]
    public async Task ReadAsync_Should_Not_Carry_An_Event_Name_Across_Frames()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of($"event: {TestStreamAdapter.StreamFailureEventName}\ndata: first\n\ndata: second\n\n");

        var frames = await scenario.ReadEventsAsync(stream);

        await Assert.That(frames[0].Name).IsEqualTo(TestStreamAdapter.StreamFailureEventName);
        await Assert.That(frames[1].Name).IsEqualTo(ServerSentEvent.DefaultName);
    }

    [Test]
    public async Task ReadAsync_Should_Join_Multi_Line_Data_Split_By_Crlf()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: first\r\ndata: second\r\n\r\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(JoinedFirstAndSecond);
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_A_Leading_Byte_Order_Mark()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("\uFEFFdata: first\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(FirstPayload);
    }

    [Test]
    public async Task ReadAsync_Should_Keep_A_Byte_Order_Mark_Inside_A_Payload()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: first\n\ndata: \uFEFFsecond\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([FirstPayload, "\uFEFFsecond"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Body_That_Ends_Mid_Line()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: {\"a\":1}\n\ndata: {\"a\"");

        _ = await Assert
            .That(async () => _ = await scenario.ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ReadAsync_Should_Read_An_Empty_Data_Value_As_A_Blank_Line()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data:\ndata: second\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(EmptyThenSecond);
    }

    [Test]
    public async Task ReadAsync_Should_Read_A_Field_Without_A_Colon_As_An_Empty_Value()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data\ndata: second\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(EmptyThenSecond);
    }

    [Test]
    public async Task ReadAsync_Should_Yield_Nothing_For_An_Empty_Body()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(string.Empty);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Skip_A_Frame_Carrying_No_Data()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(": heartbeat\n\n" + KeptFrame);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo(KeptPayload);
    }

    [Test]
    public async Task ReadAsync_Should_Reassemble_A_Frame_Split_Across_Chunks()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: {\"a", "\":1}\n", "\ndata: {\"a\":2}\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([JsonFirstPayload, JsonSecondPayload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_The_Next_Frame_After_A_Data_Line_Longer_Than_The_Read_Buffer()
    {
        var scenario = new ServerSentEventReaderScenario();
        var first = new string('x', 9000);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(WireBodyData.Frames(first, "next")));

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([first, "next"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Normalize_Crlf_Line_Endings()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: first\r\n\r\ndata: second\r\n\r\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([FirstPayload, SecondPayload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Normalize_A_Carriage_Return_Split_Across_Chunks()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: first\r", "\n\r\ndata: second\r\n\r\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([FirstPayload, SecondPayload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Flush_A_Trailing_Frame_Without_Its_Blank_Line()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data: last\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames.Single()).IsEqualTo("last");
    }

    [Test]
    public async Task ReadAsync_Should_Strip_Exactly_One_Space_After_The_Field_Name()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of("data:  padded\n\ndata:tight\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([PaddedPayload, "tight"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_A_Leading_Byte_Order_Mark_Split_Into_Single_Bytes()
    {
        var scenario = new ServerSentEventReaderScenario();
        // WHATWG HTML, Server-sent events, Parsing an event stream: ignore one leading U+FEFF.
        using var stream = ChunkedStream.OfBytes(LeadingBomAFrame, chunkSize: 1);

        await scenario.AssertSingleFrameAsync(stream, ServerSentEvent.DefaultName, APayload);
    }

    [Test]
    public async Task ReadAsync_Should_Yield_Nothing_For_A_Body_Containing_Only_A_Byte_Order_Mark()
    {
        var scenario = new ServerSentEventReaderScenario();
        // WHATWG HTML, Server-sent events, Parsing an event stream: ignore one leading U+FEFF.
        using var stream = ChunkedStream.Of(ByteOrderMark);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Not_Skip_A_Byte_Order_Mark_At_The_Start_Of_A_Later_Read()
    {
        var scenario = new ServerSentEventReaderScenario();
        // WHATWG HTML, Server-sent events, Parsing an event stream: only one leading U+FEFF is ignored.
        using var stream = ChunkedStream.Of(AFrame, "\uFEFFdata: b\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([APayload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    // WHATWG HTML, Server-sent events, Parsing an event stream: CR alone ends a line.
    [Arguments("data: a\rdata: b\r\r", new[] { JoinedAB })]
    // WHATWG HTML, Server-sent events, Parsing an event stream: LF then CR ends two lines.
    [Arguments("data: a\n\rdata: b\n\n", new[] { APayload, BPayload })]
    public async Task ReadAsync_Should_Treat_Carriage_Return_As_A_Line_Terminator(string body, string[] expected)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(body);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    // WHATWG HTML, Server-sent events, Parsing an event stream: CRLF is one terminator, even across reads.
    [Arguments(new[] { ACarriageReturnLine, "\ndata: b\n\n" }, new[] { JoinedAB })]
    // WHATWG HTML, Server-sent events, Parsing an event stream: CR alone terminates; the next byte is retained.
    [Arguments(new[] { ACarriageReturnLine, "data: b\n\n" }, new[] { JoinedAB })]
    public async Task ReadAsync_Should_Pair_Only_A_Line_Feed_After_A_Carriage_Return_Across_Reads(
        string[] chunks, string[] expected)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(chunks);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Flush_A_Trailing_Frame_Ending_In_A_Carriage_Return()
    {
        var scenario = new ServerSentEventReaderScenario();
        // SDK end-of-body policy: a terminated data line is whole even without a closing blank line.
        // Unlike WHATWG dispatch, this contract flushes the trailing frame.
        using var stream = ChunkedStream.Of(ACarriageReturnLine);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([APayload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Ignore_A_Data_Field_With_The_Wrong_Case()
    {
        var scenario = new ServerSentEventReaderScenario();
        // WHATWG HTML, Server-sent events, Interpreting an event stream: field names are case-sensitive;
        // a frame with no data dispatches nothing.
        using var stream = ChunkedStream.Of("Data: x\n\n");

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    // WHATWG HTML, Server-sent events, Interpreting an event stream: the last event field sets the type.
    [Arguments("event: first\nevent: second\ndata: a\n\n", SecondPayload)]
    // WHATWG HTML, Server-sent events, Interpreting an event stream: an empty event type means message.
    [Arguments("event:\ndata: a\n\n", ServerSentEvent.DefaultName)]
    // WHATWG HTML, Server-sent events, Interpreting an event stream: an empty event value resets the type.
    [Arguments("event: first\nevent:\ndata: a\n\n", ServerSentEvent.DefaultName)]
    [Arguments("event: message\ndata: a\n\n", ServerSentEvent.DefaultName)]
    [Arguments("event\ndata: a\n\n", ServerSentEvent.DefaultName)]
    public async Task ReadAsync_Should_Name_A_Frame_From_The_Last_Event_Value(string body, string expectedName)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(body);

        await scenario.AssertSingleFrameAsync(stream, expectedName, APayload);
    }

    [Test]
    [Arguments("event: x\ndata: a\ndata: b\n\n", "x", JoinedAB)]
    [Arguments("data: a\nevent: x\n\n", "x", APayload)]
    [Arguments("event: x\ndata: a\nevent: y\ndata: b\n\n", "y", JoinedAB)]
    public async Task ReadAsync_Should_Preserve_The_Name_After_Data_Relocation(string body, string name, string data)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(body);

        await scenario.AssertSingleFrameAsync(stream, name, data);
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_The_Name_Through_Repeated_Data_Growth()
    {
        var scenario = new ServerSentEventReaderScenario();
        var name = new string('n', 200);
        var first = new string('a', 100);
        var second = new string('b', 400);
        var third = new string('c', 1000);
        using var stream = ChunkedStream.Of($"event: {name}\ndata: {first}\ndata: {second}\ndata: {third}\n\n");

        await scenario.AssertSingleFrameAsync(stream, name, $"{first}\n{second}\n{third}");
    }

    [Test]
    [Arguments(FrameDelimiter)]
    [Arguments("\r\r")]
    [Arguments("\r\n\r\n")]
    [Arguments("\n\r")]
    [Arguments("\r\n\n")]
    public async Task ReadAsync_Should_Preserve_Every_Closing_Delimiter_Split(string delimiters)
    {
        var scenario = new ServerSentEventReaderScenario();
        ArgumentNullException.ThrowIfNull(delimiters);

        for (var split = 0; split <= delimiters.Length; split++)
        {
            using var stream = ChunkedStream.Of($"data: a{delimiters[..split]}", $"{delimiters[split..]}data: b\n\n");

            var frames = await scenario.ReadAllAsync(stream);

            await Assert.That(frames).IsEquivalentTo([APayload, BPayload], StringComparer.Ordinal, CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("\uFEFF\uFEFF")]
    [Arguments("\uFEFE")]
    [Arguments("\uFFFD")]
    public async Task ReadAsync_Should_Treat_Repeated_And_Valid_Near_Boms_As_Field_Text(string prefix)
    {
        var scenario = new ServerSentEventReaderScenario();
        var bytes = Encoding.UTF8.GetBytes(prefix);
        for (var split = 1; split <= bytes.Length; split++)
        {
            using var stream = ChunkedStream.OfBytes([.. bytes.Take(split)],
                [.. bytes.Skip(split), .. Encoding.UTF8.GetBytes($"data: ignored\n\n{KeptFrame}")]);

            var frames = await scenario.ReadAllAsync(stream);

            await Assert.That(frames).IsEquivalentTo([KeptPayload], StringComparer.Ordinal, CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments("a:b")]
    [Arguments("\tvalue")]
    [Arguments("\0value")]
    [Arguments("\u00A0value")]
    [Arguments(PaddedPayload)]
    public async Task ReadAsync_Should_Preserve_Field_Value_Characters(string value)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of($"event: {value}\ndata: {value}\n\n");

        await scenario.AssertSingleFrameAsync(stream, value, value);
    }

    [Test]
    [Arguments("Event: x")]
    [Arguments("datax: x")]
    [Arguments("data : x")]
    [Arguments("unknown")]
    [Arguments(":")]
    public async Task ReadAsync_Should_Match_Only_Exact_Field_Names(string ignored)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of($"{ignored}\n{KeptFrame}");

        await scenario.AssertSingleFrameAsync(stream, ServerSentEvent.DefaultName, KeptPayload);
    }

    [Test]
    [Arguments("event: x\n\n")]
    [Arguments(EventXLine)]
    [Arguments(EventXEmptyFrame)]
    public async Task ReadAsync_Should_Not_Dispatch_An_Event_Only_Body(string body)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(body);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    public async Task ReadAsync_Should_Discard_An_Event_Only_Frames_Name()
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(EventXEmptyFrame, KeptFrame);

        await scenario.AssertSingleFrameAsync(stream, ServerSentEvent.DefaultName, KeptPayload);
    }

    [Test]
    [Arguments("data: a\r\n", APayload)]
    [Arguments("data:\n", "")]
    [Arguments("data\n", "")]
    [Arguments("data:\r\n", "")]
    [Arguments("data:\n\n", "")]
    [Arguments("data\n\n", "")]
    public async Task ReadAsync_Should_Dispatch_Terminated_And_Empty_Data_At_Eof(string body, string payload)
    {
        var scenario = new ServerSentEventReaderScenario();
        using var stream = ChunkedStream.Of(body);

        var frames = await scenario.ReadAllAsync(stream);

        await Assert.That(frames).IsEquivalentTo([payload], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(UnfinishedData)]
    [Arguments("event: cut")]
    [Arguments(": cut")]
    [Arguments("unknown: cut")]
    public async Task ReadAsync_Should_Report_Truncation_After_Its_Delivered_Prefix(string unfinished)
    {
        using var stream = ChunkedStream.Of($"data: first\n\n{unfinished}");
        await using var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator();

        await Assert.That(await frames.MoveNextAsync()).IsTrue();
        await Assert.That(frames.Current.Name).IsEqualTo(ServerSentEvent.DefaultName);
        await Assert.That(TestFrames.ReadText(frames.Current.Data)).IsEqualTo(FirstPayload);
        _ = await Assert.That(async () => _ = await frames.MoveNextAsync()).Throws<OpenCodeTransportException>()
            .WithMessage(TruncationMessage, StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadAsync_Should_Dispatch_A_Cr_Completed_Frame_Before_Another_Read()
    {
        using var stream = new GatedStream(Encoding.UTF8.GetBytes("data: a\r\rdata: b\r\r"), chunkCount: 2);
        await using var frames = new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).GetAsyncEnumerator();
        var firstMove = frames.MoveNextAsync().AsTask();
        _ = await stream.WaitForReadAsync(HangGuard.Bound);
        stream.Release();
        var secondRead = stream.WaitForReadAsync(HangGuard.Bound);
        var completedFirst = await Task.WhenAny(firstMove, secondRead);

        // Release and observe both moves even if a delayed-CR mutant asks for the second read first.
        stream.Release();
        await Assert.That(await firstMove).IsTrue();
        var first = TestFrames.ReadText(frames.Current.Data);
        await Assert.That(await frames.MoveNextAsync()).IsTrue();
        var second = TestFrames.ReadText(frames.Current.Data);
        _ = await secondRead;

        await Assert.That(completedFirst).IsSameReferenceAs(firstMove);
        await Assert.That(first).IsEqualTo(APayload);
        await Assert.That(second).IsEqualTo(BPayload);
    }
}
