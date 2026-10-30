using System.Net.WebSockets;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Ptys.Support;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Ptys;

public sealed class PtySessionTests
{
    [Test]
    public async Task WriteAsync_Should_Use_The_Connections_Configured_Send_Timer()
    {
        var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var session = new PtySession(socket, TimeProvider.System, TimeSpan.FromMilliseconds(50));
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var failure = await Assert.That(async () => await session.WriteAsync("input", watchdog.Token))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.InnerException).IsTypeOf<TimeoutException>();
    }

    private const string SessionNotFoundReason = "session exited";

    private const WebSocketCloseStatus SessionNotFound = (WebSocketCloseStatus)4404;

    public static IEnumerable<Func<string>> MalformedControlBodies() =>
    [
        static () => PtyFrameData.TruncatedControlJson,
        static () => PtyFrameData.CursorlessControlJson,
        static () => PtyFrameData.NonNumericCursorControlJson,
        static () => string.Empty,
    ];

    [Test]
    public async Task ReadAsync_Should_Yield_The_Replay_Chunks_In_Order()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text("chunk-one")
            .Text("chunk-two")
            .Text("chunk-three")
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.OfType<PtyOutputFrame>().Select(static frame => frame.Text).ToArray())
            .IsEquivalentTo(["chunk-one", "chunk-two", "chunk-three"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Read_The_Control_Frame_As_The_Exact_Cursor()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text("replayed")
            .Binary(PtyFrameData.ControlFrame(PtyFrameData.CursorControlJson))
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(((PtyOutputFrame)frames[0]).Text).IsEqualTo("replayed");
        await Assert.That(((PtyCursorFrame)frames[1]).Cursor).IsEqualTo(PtyFrameData.CursorValue);
    }

    [Test]
    [MethodDataSource(nameof(MalformedControlBodies))]
    public async Task ReadAsync_Should_Refuse_A_Malformed_Control_Frame(string body)
    {
        var socket = new ScriptedTerminalWebSocket().Binary(PtyFrameData.ControlFrame(body));
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("control frame");
    }

    [Test]
    public async Task ReadAsync_Should_Assemble_A_Fragmented_Text_Message_Once()
    {
        var socket = new ScriptedTerminalWebSocket()
            .TextFragments("frag-", "men", "ted")
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(((PtyOutputFrame)frames[0]).Text).IsEqualTo("frag-mented");
    }

    [Test]
    public async Task ReadAsync_Should_Assemble_A_Fragmented_Control_Frame_Once()
    {
        var frame = PtyFrameData.ControlFrame(PtyFrameData.CursorControlJson);
        var socket = new ScriptedTerminalWebSocket()
            .BinaryFragments(frame, splitAt: 4)
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(((PtyCursorFrame)frames[0]).Cursor).IsEqualTo(PtyFrameData.CursorValue);
    }

    [Test]
    public async Task ReadAsync_Should_Decode_A_Broken_Surrogate_With_Replacement()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Binary(PtyFrameData.UnpairedSurrogate)
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        var output = (PtyOutputFrame)frames.Single();
        await Assert.That(output.Text.Length).IsGreaterThan(0);
        await Assert.That(output.Text.AsEnumerable()).All().Satisfy(character => character.IsEqualTo('\uFFFD'));
    }

    [Test]
    public async Task ReadAsync_Should_Read_A_Binary_Message_Without_The_Marker_As_Output()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Binary(Encoding.UTF8.GetBytes("raw-output"))
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        await Assert.That(((PtyOutputFrame)frames.Single()).Text).IsEqualTo("raw-output");
    }

    [Test]
    public async Task ReadAsync_Should_End_The_Enumeration_On_A_Normal_Close()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text("last")
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Session_Not_Found_Close()
    {
        var socket = new ScriptedTerminalWebSocket().Closing(SessionNotFound, SessionNotFoundReason);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await AssertSessionNotFoundAsync(session);
        await Assert.That(failure.Message).Contains(SessionNotFoundReason);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Session_Not_Found_Close_With_An_Empty_Reason()
    {
        // A real ClientWebSocket reports an empty description, not null, when the peer sent no
        // reason text; the policy must treat both the same rather than rendering a hollow "4404 ()".
        var socket = new ScriptedTerminalWebSocket().Closing(SessionNotFound, string.Empty);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await AssertSessionNotFoundAsync(session);
        await Assert.That(failure.Message).DoesNotContain("()", StringComparison.Ordinal);
    }

    private static async Task<OpenCodeTransportException> AssertSessionNotFoundAsync(PtySession session)
    {
        var failure = await Assert.That(async () => _ = await ReadAllAsync(session))
            .Throws<OpenCodeTransportException>();
        await Assert.That(failure!.Message).Contains("4404");
        return failure;
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Abnormal_Close()
    {
        var socket = new ScriptedTerminalWebSocket().Closing(WebSocketCloseStatus.ProtocolError);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("1002");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Second_Concurrent_Enumeration()
    {
        var socket = new ScriptedTerminalWebSocket().Text("first").Parking();
        await using var session = new PtySession(socket, new ManualTimeProvider());
        var first = session.ReadAsync().GetAsyncEnumerator(CancellationToken.None);
        await using var enumeration = first.ConfigureAwait(false);
        await Assert.That(await first.MoveNextAsync()).IsTrue();

        var second = session.ReadAsync().GetAsyncEnumerator(CancellationToken.None);

        _ = await Assert.That(async () => _ = await second.MoveNextAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ReadAsync_Should_Allow_A_Second_Enumeration_After_The_First_Breaks()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text("first")
            .Text("second")
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = new PtySession(socket, new ManualTimeProvider());

        await using (var first = session.ReadAsync().GetAsyncEnumerator())
        {
            await Assert.That(await first.MoveNextAsync()).IsTrue();
            await Assert.That(((PtyOutputFrame)first.Current).Text).IsEqualTo("first");
        }

        var second = await ReadAllAsync(session);

        await Assert.That(((PtyOutputFrame)second.Single()).Text).IsEqualTo("second");
    }

    [Test]
    public async Task ReadAsync_Should_Report_A_Caller_Cancellation_As_Cancellation()
    {
        var socket = new ScriptedTerminalWebSocket().Parking();
        await using var session = new PtySession(socket, new ManualTimeProvider());
        using var cancellation = new CancellationTokenSource();
        var enumerator = session.ReadAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = enumerator.MoveNextAsync();
        await socket.Parked;

        await cancellation.CancelOnWorkerAsync();

        _ = await Assert.That(async () => _ = await pending).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ReadAsync_Should_Report_A_Socket_Fault_As_A_Transport_Failure()
    {
        var socket = new ScriptedTerminalWebSocket().Faulting(new WebSocketException("connection reset"));
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("PTY WebSocket");
    }

    [Test]
    public async Task DisposeAsync_Should_End_A_Pending_Read_As_A_Normal_End()
    {
        var socket = new ScriptedTerminalWebSocket().Text("live").Parking();
        var session = new PtySession(socket, new ManualTimeProvider());
        var enumerator = session.ReadAsync().GetAsyncEnumerator(CancellationToken.None);
        await using var enumeration = enumerator.ConfigureAwait(false);
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        var pending = enumerator.MoveNextAsync();
        await socket.Parked;

        await session.DisposeAsync();

        await Assert.That(await pending).IsFalse();
    }

    [Test]
    public async Task WriteAsync_Should_Encode_The_Input_As_Utf8()
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        await session.WriteAsync("héllo→\n");

        await Assert.That(socket.SentText).IsEquivalentTo(["héllo→\n"], CollectionOrdering.Matching);
        await Assert.That(socket.SentMessages.Single().Length).IsEqualTo(Encoding.UTF8.GetByteCount("héllo→\n"));
    }

    [Test]
    public async Task WriteAsync_Should_Send_A_Text_Message()
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        await session.WriteAsync("ls\r");

        await Assert.That(socket.SentMessageTypes.Single()).IsEqualTo(WebSocketMessageType.Text);
        await Assert.That(socket.SentText.Single()).IsEqualTo("ls\r");
    }

    [Test]
    public async Task WriteAsync_Should_Serialize_Concurrent_Sends()
    {
        var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var first = session.WriteAsync("first");
        await socket.SendEntered;
        var second = session.WriteAsync("second");
        await Assert.That(socket.SentMessages.Count).IsEqualTo(1);

        socket.ReleaseSends();
        await Task.WhenAll(first, second);

        await Assert.That(socket.MaxConcurrentSends).IsEqualTo(1);
        await Assert.That(socket.SentText).IsEquivalentTo(["first", "second"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WriteAsync_Should_Refuse_A_Null_Input()
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        _ = await Assert.That(async () => await session.WriteAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WriteAsync_Should_Refuse_A_Write_After_Disposal()
    {
        var socket = new ScriptedTerminalWebSocket();
        var session = await CreateDisposedSessionAsync(socket);

        _ = await Assert.That(async () => await session.WriteAsync("late")).Throws<ObjectDisposedException>();
    }

    /// <summary>The line every submit test sends; a submit adds the terminator, never the line.</summary>
    private const string SubmittedLine = "echo hello";

    /// <summary>The carriage return a terminal's Enter key sends, pinned here rather than derived.</summary>
    private const string Enter = "\r";

    /// <summary>Lines a submit refuses: one submit is one Enter, so a break is WriteAsync's business.</summary>
    public static IEnumerable<Func<string>> LinesCarryingABreak() =>
    [
        static () => SubmittedLine + "\r",
        static () => SubmittedLine + "\n",
        static () => SubmittedLine + "\r\necho again",
    ];

    [Test]
    public async Task SubmitAsync_Should_Send_The_Line_Terminated_By_Enter()
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        await session.SubmitAsync(SubmittedLine);

        await Assert.That(socket.SentMessageTypes.Single()).IsEqualTo(WebSocketMessageType.Text);
        await Assert.That(socket.SentText.Single()).IsEqualTo(SubmittedLine + Enter);
    }

    [Test]
    public async Task SubmitAsync_Should_Send_A_Bare_Enter_For_An_Empty_Line()
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        await session.SubmitAsync(string.Empty);

        await Assert.That(socket.SentText.Single()).IsEqualTo(Enter);
    }

    [Test]
    public async Task SubmitAsync_Should_Refuse_A_Null_Line()
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        _ = await Assert.That(async () => await session.SubmitAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    [MethodDataSource(nameof(LinesCarryingABreak))]
    public async Task SubmitAsync_Should_Refuse_A_Line_Carrying_A_Break(string line)
    {
        var socket = new ScriptedTerminalWebSocket();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await Assert.That(async () => await session.SubmitAsync(line)).Throws<ArgumentException>();

        // A submit is one Enter: an embedded break would run a command the caller never wrote.
        await Assert.That(failure!.ParamName).IsEqualTo("line");
        await Assert.That(socket.SentMessages).IsEmpty();
    }

    [Test]
    public async Task SubmitAsync_Should_Refuse_A_Submit_After_Disposal()
    {
        var socket = new ScriptedTerminalWebSocket();
        var session = await CreateDisposedSessionAsync(socket);

        _ = await Assert.That(async () => await session.SubmitAsync(SubmittedLine)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task SubmitAsync_Should_Report_A_Failed_Send_As_A_Transport_Failure()
    {
        var socket = new ScriptedTerminalWebSocket().FailingNextSendWith(new WebSocketException("connection reset"));
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var failure = await Assert.That(async () => await session.SubmitAsync(SubmittedLine))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("PTY WebSocket");
        await Assert.That(failure.InnerException).IsTypeOf<WebSocketException>();
    }

    [Test]
    public async Task SubmitAsync_Should_Serialize_Behind_An_Outstanding_Write()
    {
        var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var session = new PtySession(socket, new ManualTimeProvider());

        var write = session.WriteAsync("partial");
        await socket.SendEntered;
        var submit = session.SubmitAsync(SubmittedLine);
        await Assert.That(socket.SentMessages.Count).IsEqualTo(1);

        socket.ReleaseSends();
        await Task.WhenAll(write, submit);

        await Assert.That(socket.MaxConcurrentSends).IsEqualTo(1);
        await Assert.That(socket.SentText).IsEquivalentTo(["partial", SubmittedLine + Enter], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DisposeAsync_Should_Let_An_In_Flight_Write_Finish_Cleanly()
    {
        var socket = new ScriptedTerminalWebSocket().GatingSends();
        var session = CreateSession(socket);
        var write = session.WriteAsync("in-flight");
        await socket.SendEntered;

        var dispose = session.DisposeAsync();
        socket.ReleaseSends();
        await write;
        await dispose;

        // The write owned the gate when disposal began: it must complete on its own terms rather
        // than fail on a semaphore the disposal tore down under it.
        await Assert.That(socket.SentText).IsEquivalentTo(["in-flight"], CollectionOrdering.Matching);
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_Should_Refuse_A_Queued_Write_Rather_Than_Strand_It()
    {
        var socket = new ScriptedTerminalWebSocket().GatingSends();
        var session = new PtySession(socket, new ManualTimeProvider());
        var first = session.WriteAsync("first");
        await socket.SendEntered;
        var queued = session.WriteAsync("queued");

        var dispose = session.DisposeAsync();
        socket.ReleaseSends();
        await first;
        await dispose;

        // The queued writer was parked on the send gate when disposal landed; it has to wake and
        // be refused, because a disposed semaphore never releases a pending async waiter. Awaited
        // directly rather than through an assertion lambda so the wait stays in this context.
        ObjectDisposedException? refusal = null;
        try
        {
            await queued;
        }
        catch (ObjectDisposedException exception)
        {
            refusal = exception;
        }

        await Assert.That(refusal).IsNotNull();
        await Assert.That(socket.SentText).IsEquivalentTo(["first"], CollectionOrdering.Matching);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_Should_Not_Close_While_A_Send_Is_Outstanding()
    {
        var socket = new ScriptedTerminalWebSocket().GatingSends();
        var session = CreateSession(socket);
        var write = session.WriteAsync("in-flight");
        await socket.SendEntered;

        var dispose = session.DisposeAsync();

        // A close-output frame is a send, and the socket allows one outstanding send: the close
        // must wait behind the write rather than race it.
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(0);
        socket.ReleaseSends();
        await write;
        await dispose;
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_Should_Tear_The_Socket_Down_When_The_Graceful_Close_Fails()
    {
        var socket = new ScriptedTerminalWebSocket().FailingCloseWith(new InvalidOperationException("wrong state"));
        var session = new PtySession(socket, new ManualTimeProvider());

        await session.DisposeAsync();

        // A close that fails in a way the write plane does not own must still not escape disposal
        // with the socket left alive.
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_Should_Close_Gracefully_And_Stay_Idempotent()
    {
        var socket = new ScriptedTerminalWebSocket();
        var session = new PtySession(socket, new ManualTimeProvider());

        await session.DisposeAsync();
        await session.DisposeAsync();

        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Session_Mock_Seam_Should_Stay_Overridable()
    {
        await using var session = new MockPtySession();

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(((PtyOutputFrame)frames[0]).Text).IsEqualTo("mocked");
        await Assert.That(((PtyCursorFrame)frames[1]).Cursor).IsEqualTo(PtyFrameData.CursorValue);
    }

    /// <summary>
    /// The mocking seam is only usable if the frames an override yields can be built from outside
    /// this assembly. This test project is a friend, so the compiler cannot tell an internal
    /// constructor from a public one — reflection is what pins the accessibility an external
    /// consumer actually depends on.
    /// </summary>
    [Test]
    public async Task Frame_Constructors_Should_Be_Reachable_Without_Friend_Access()
    {
        var output = typeof(PtyOutputFrame).GetConstructor([typeof(string)]);
        var cursor = typeof(PtyCursorFrame).GetConstructor([typeof(long)]);

        await Assert.That(output!.IsPublic).IsTrue();
        await Assert.That(cursor!.IsPublic).IsTrue();
    }

    [Test]
    public async Task Session_Mock_Seam_Should_Fail_Instructively_Without_An_Override()
    {
        await using var session = new UnoverriddenPtySession();

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<InvalidOperationException>();

        await Assert.That(failure!.Message).Contains("mocking constructor");
    }

    private static PtySession CreateSession(ITerminalWebSocket socket) => new(socket, new ManualTimeProvider());

    private static async Task<PtySession> CreateDisposedSessionAsync(ITerminalWebSocket socket)
    {
        var session = CreateSession(socket);
        await session.DisposeAsync();
        return session;
    }

    private static async Task<IReadOnlyList<PtyFrame>> ReadAllAsync(PtySession session)
    {
        var frames = new List<PtyFrame>();
        await foreach (var frame in session.ReadAsync())
        {
            frames.Add(frame);
        }

        return frames;
    }

    private sealed class MockPtySession : PtySession
    {
        public override IAsyncEnumerable<PtyFrame> ReadAsync(CancellationToken cancellationToken = default) =>
            Mocked();

        /// <summary>
        /// Builds both frame kinds through their public doors, exactly as a consumer outside this
        /// assembly would write the same override.
        /// </summary>
        private static async IAsyncEnumerable<PtyFrame> Mocked()
        {
            await Task.Yield();
            yield return new PtyOutputFrame("mocked");
            yield return new PtyCursorFrame(PtyFrameData.CursorValue);
        }
    }

    private sealed class UnoverriddenPtySession : PtySession
    {
    }
}
