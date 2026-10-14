using System.Net.WebSockets;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public sealed class PersistentPtySessionTests
{
    [Test]
    public async Task AttachAsync_Should_Release_The_Receiver_When_Canceled_Before_Attachment()
    {
        var socket = new ScriptedTerminalWebSocket().Parking();
        using var cancellation = new CancellationTokenSource();
        // The finally below cancels and awaits this attachment before the source is disposed.
#pragma warning disable CA2025 // The analyzer does not recognize this Task<disposable> join in finally.
        var pending = PersistentPtySession.AttachAsync(socket, PtyId, cancellation.Token);
#pragma warning restore CA2025
        OperationCanceledException? failure = null;
        try
        {
            await socket.Parked;
        }
        finally
        {
            await cancellation.CancelOnWorkerAsync();
            try
            {
                var unexpected = await pending;
                await unexpected.DisposeAsync();
            }
            catch (OperationCanceledException exception)
            {
                failure = exception;
            }
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task AttachAsync_Should_Detach_The_Connect_Token_After_Returning_The_Session()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson)
            .Pausing().Binary(PersistentPtyFrameData.Output("after attach")).Closing(WebSocketCloseStatus.NormalClosure);
        using var cancellation = new CancellationTokenSource();
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, cancellation.Token);
        await cancellation.CancelOnWorkerAsync();
        await session.WriteAsync(PersistentPtyFrameData.Output("input"));
        socket.ReleaseReceives();

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(((PersistentPtyOutputFrame)frames[0]).Data.ToArray())
            .IsEquivalentTo(PersistentPtyFrameData.Output("after attach"));
        await Assert.That(socket.DisposeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ResizeAsync_Should_Use_The_Connections_Configured_Send_Timer()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson).GatingSends();
        await using var session = await PersistentPtySession.AttachAsync(
            socket, PtyId, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var failure = await Assert.That(async () => await session.ResizeAsync(100, 30, watchdog.Token))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.InnerException).IsTypeOf<TimeoutException>();
    }

    private const string PtyId = "pty_persistent_7";

    private const WebSocketCloseStatus TerminalUnavailable = (WebSocketCloseStatus)4404;

    private static readonly byte[] ResizeCheckpoint = [0x1B, 0x63];

    public static IEnumerable<Func<(string Control, Func<PersistentPtyFrame, Task> Verify)>> ControlFrames() =>
    [
        static () => (PersistentPtyFrameData.ResizedJson, static async frame =>
        {
            var resized = (PersistentPtyResizedFrame)frame;
            await Assert.That(resized.Cols).IsEqualTo(120);
            await Assert.That(resized.Rows).IsEqualTo(40);
            await Assert.That(resized.Generation).IsEqualTo(4L);
            await Assert.That(resized.Checkpoint.ToArray()).IsEquivalentTo(ResizeCheckpoint);
        }),
        static () => (PersistentPtyFrameData.ExitedJson, static async frame =>
        {
            var exited = (PersistentPtyExitedFrame)frame;
            await Assert.That(exited.ExitCode).IsEqualTo(0);
            await Assert.That(exited.FinalOffset).IsEqualTo(99L);
        }),
        static () => (PersistentPtyFrameData.ExitedWithoutCodeJson, static async frame =>
        {
            var exited = (PersistentPtyExitedFrame)frame;
            await Assert.That(exited.ExitCode).IsNull();
            await Assert.That(exited.FinalOffset).IsEqualTo(99L);
        }),
        static () => (PersistentPtyFrameData.ControllerChangedJson, static async frame =>
        {
            var changed = (PersistentPtyControllerChangedFrame)frame;
            await Assert.That(changed.AttachmentId).IsEqualTo("att_9");
            await Assert.That(changed.Generation).IsEqualTo(5L);
        }),
        static () => (PersistentPtyFrameData.ControllerChangedWithoutAttachmentIdJson, static async frame =>
        {
            // Upstream omits the member outright when the terminal has no controller, so the
            // absent-member arm is a real wire state, not a defensive read of a promised member.
            var changed = (PersistentPtyControllerChangedFrame)frame;
            await Assert.That(changed.AttachmentId).IsNull();
            await Assert.That(changed.Generation).IsEqualTo(6L);
        }),
        static () => (PersistentPtyFrameData.TitleChangedJson, static async frame =>
        {
            var changed = (PersistentPtyTitleChangedFrame)frame;
            await Assert.That(changed.Title).IsEqualTo("vim");
        }),
        static () => (PersistentPtyFrameData.ForegroundProcessChangedJson, static async frame =>
        {
            var changed = (PersistentPtyForegroundProcessChangedFrame)frame;
            await Assert.That(changed.Process).IsNull();
        }),
        static () => (PersistentPtyFrameData.ForegroundProcessChangedWithProcessJson, static async frame =>
        {
            var changed = (PersistentPtyForegroundProcessChangedFrame)frame;
            await Assert.That(changed.Process).IsEqualTo("vim");
        }),
    ];

    [Test]
    public async Task AttachAsync_Should_Consume_The_Attached_Frame_And_Expose_The_Attachment()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Closing(WebSocketCloseStatus.NormalClosure);

        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await Assert.That(session.Attachment.AttachmentId).IsEqualTo("att_1");
        await Assert.That(session.Attachment.Role).IsEqualTo(PersistentPtyRole.Controller);
        await Assert.That(session.Attachment.Info.Id).IsEqualTo(PtyId);
        await Assert.That(session.Attachment.Replay.EndOffset).IsEqualTo(42L);
        await Assert.That(await ReadAllAsync(session)).IsEmpty();
    }

    [Test]
    public async Task AttachAsync_Should_Refuse_A_Terminal_Unavailable_Close_Before_Attached()
    {
        var socket = new ScriptedTerminalWebSocket().Closing(TerminalUnavailable, PersistentPtyFrameData.TerminalUnavailableReason);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("4404");
        await Assert.That(failure.Message).Contains("daemon");
    }

    [Test]
    public async Task AttachAsync_Should_Refuse_A_Raw_Input_Protocol()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedRawProtocolJson);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("input protocol");
        await Assert.That(failure.Message).Contains("protocol 1");

        // An attach that never produced a session still owns the socket it was handed, so the
        // refusal has to leave the connection released rather than leaked to the finalizer.
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task AttachAsync_Should_Refuse_A_First_Frame_That_Is_Not_Attached()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.ReplayCompleteJson);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("attached");

        // The second distinct exit through the same release path: a first frame the SDK read but
        // did not want must release the socket exactly as the protocol refusal above does.
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task AttachAsync_Should_Name_The_Frame_Kind_When_The_Attachment_Id_Is_Null()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedNullAttachmentIdJson);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        // A wire null for a promised string is a member-level failure of a frame whose type the
        // SDK already read, so it is named the same way an unreadable member is - not left to
        // surface as a bare null attachment identity.
        await Assert.That(failure!.Message).Contains("'attached'");
        await Assert.That(failure.Message).Contains("could not read");
    }

    [Test]
    public async Task AttachAsync_Should_Name_The_Frame_Kind_When_The_Role_Is_Null()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedNullRoleJson);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("'attached'");
        await Assert.That(failure.Message).Contains("could not read");
    }

    [Test]
    public async Task AttachAsync_Should_Name_The_Frame_Kind_When_The_Role_Is_Not_Recognized()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedUnknownRoleJson);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        // The wire spells 'controller' and 'observer' and nothing else, so a third spelling is a
        // protocol deviation - never a connection quietly reported as the controller it is not.
        await Assert.That(failure!.Message).Contains("'attached'");
        await Assert.That(failure.Message).Contains("could not read");
    }

    [Test]
    public async Task AttachAsync_Should_Refuse_A_Normal_Close_Before_Attached()
    {
        var socket = new ScriptedTerminalWebSocket().Closing(WebSocketCloseStatus.NormalClosure);

        var failure = await Assert
            .That(async () => _ = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("attached");
    }

    [Test]
    public async Task AttachAsync_Should_Expose_A_Truncated_Observer_Replay()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedObserverJson);

        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await Assert.That(session.Attachment.Role).IsEqualTo(PersistentPtyRole.Observer);
        await Assert.That(session.Attachment.Replay.Truncated).IsTrue();
        await Assert.That(session.Attachment.Replay.RequestedOffset).IsEqualTo(10L);
        await Assert.That(session.Attachment.Replay.AvailableOffset).IsEqualTo(20L);
        await Assert.That(session.Attachment.Generation).IsEqualTo(3L);
    }

    [Test]
    public async Task ReadAsync_Should_Yield_Output_As_Bytes_And_The_Replay_Bracket_In_Order()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Binary(PersistentPtyFrameData.Output("$ "))
            .Text(PersistentPtyFrameData.ReplayCompleteJson)
            .Binary(PersistentPtyFrameData.Output("hello\n"))
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var frames = await ReadAllAsync(session);

        await Assert.That(frames.Count).IsEqualTo(3);
        await Assert.That(((PersistentPtyOutputFrame)frames[0]).Data.ToArray())
            .IsEquivalentTo(PersistentPtyFrameData.Output("$ "));
        await Assert.That(((PersistentPtyReplayCompleteFrame)frames[1]).EndOffset).IsEqualTo(42L);
        await Assert.That(((PersistentPtyOutputFrame)frames[2]).Data.ToArray())
            .IsEquivalentTo(PersistentPtyFrameData.Output("hello\n"));
    }

    [Test]
    [MethodDataSource(nameof(ControlFrames))]
    public async Task ReadAsync_Should_Decode_Each_Control_Frame_Kind(
        (string Control, Func<PersistentPtyFrame, Task> Verify) control)
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(control.Control)
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var frames = await ReadAllAsync(session);

        await control.Verify(frames.Single());
    }

    [Test]
    public async Task ReadAsync_Should_Yield_An_Unknown_Control_Type_As_An_Unknown_Frame()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.UnknownTypeJson)
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var frames = await ReadAllAsync(session);

        var unknown = (PersistentPtyUnknownFrame)frames.Single();
        await Assert.That(unknown.Type).IsEqualTo("scrollback_trimmed");
        await Assert.That(unknown.Payload.GetProperty("bytes").GetInt32()).IsEqualTo(1024);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Control_Frame_Without_A_Type()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.TypelessJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("type");
    }

    [Test]
    public async Task ReadAsync_Should_Name_The_Frame_Kind_When_A_Typed_Control_Frame_Is_Unreadable()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.ResizedWithoutColsJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<OpenCodeTransportException>();

        // The body is a JSON object carrying a string type, so blaming the envelope would be a
        // false report: the frame kind the SDK could not read is what a reader needs to see.
        await Assert.That(failure!.Message).Contains("'resized'");
        await Assert.That(failure.Message.Contains("not a JSON object", StringComparison.Ordinal)).IsFalse();
        await Assert.That(failure.InnerException).IsNotNull();
    }

    [Test]
    public async Task ReadAsync_Should_Name_The_Frame_Kind_When_A_Title_Changed_Frame_Carries_A_Null_Title()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.TitleChangedNullTitleJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("'title_changed'");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_Truncated_Control_Json()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.TruncatedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("control frame");
    }

    [Test]
    public async Task ReadAsync_Should_Assemble_A_Fragmented_Output_Message_Once()
    {
        var output = PersistentPtyFrameData.Output("frag-mented");
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .BinaryFragments(output, splitAt: 5)
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var frames = await ReadAllAsync(session);

        await Assert.That(((PersistentPtyOutputFrame)frames.Single()).Data.ToArray()).IsEquivalentTo(output);
    }

    [Test]
    public async Task ReadAsync_Should_Assemble_A_Fragmented_Control_Frame_Once()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .TextFragments(PersistentPtyFrameData.ResizedJson[..20], PersistentPtyFrameData.ResizedJson[20..])
            .Closing(WebSocketCloseStatus.NormalClosure);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var frames = await ReadAllAsync(session);

        var resized = (PersistentPtyResizedFrame)frames.Single();
        await Assert.That(resized.Cols).IsEqualTo(120);
        await Assert.That(resized.Checkpoint.ToArray()).IsEquivalentTo(ResizeCheckpoint);
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Terminal_Unavailable_Close_Mid_Stream()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Binary(PersistentPtyFrameData.Output("live"))
            .Closing(TerminalUnavailable, PersistentPtyFrameData.TerminalUnavailableReason);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        var frames = new List<PersistentPtyFrame>();

        var failure = await Assert.That(async () => await ReadIntoAsync(session, frames))
            .Throws<OpenCodeTransportException>();

        await Assert.That(((PersistentPtyOutputFrame)frames.Single()).Data.ToArray())
            .IsEquivalentTo(PersistentPtyFrameData.Output("live"));
        await Assert.That(failure!.Message).Contains("4404");
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_An_Abnormal_Close()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Closing(WebSocketCloseStatus.InternalServerError);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).Contains("1011");
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_The_Local_Viewport_When_The_Server_Reports_A_Resize()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.ResizedJson)
            .Parking();
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        await using var frames = session.ReadAsync().GetAsyncEnumerator();
        await Assert.That(await frames.MoveNextAsync()).IsTrue();
        var resized = (PersistentPtyResizedFrame)frames.Current;
        await Assert.That(resized.Cols).IsEqualTo(120);
        await Assert.That(resized.Rows).IsEqualTo(40);

        await session.WriteAsync(PersistentPtyFrameData.Output("ls\n"));

        await Assert.That(socket.SentMessages.Single())
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 80, 24, PersistentPtyFrameData.Output("ls\n")));
    }

    [Test]
    public async Task WriteAsync_Should_Use_The_Resize_That_Completes_Before_Its_Socket_Send()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Text(PersistentPtyFrameData.ResizedJson)
            .Parking()
            .GatingSends();
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        var resize = session.ResizeAsync(100, 30);
        await socket.SendEntered;
        var write = session.WriteAsync(PersistentPtyFrameData.Output("ls\n"));
        try
        {
            await using var frames = session.ReadAsync().GetAsyncEnumerator();
            await Assert.That(await frames.MoveNextAsync()).IsTrue();
            await Assert.That(((PersistentPtyResizedFrame)frames.Current).Cols).IsEqualTo(120);
        }
        finally
        {
            socket.ReleaseSends();
        }

        await Task.WhenAll(resize, write);

        await Assert.That(socket.SentMessages[0]).IsEquivalentTo(PersistentPtyFrameData.Framed(0, 100, 30, []));
        await Assert.That(socket.SentMessages[1])
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 100, 30, PersistentPtyFrameData.Output("ls\n")));
        await Assert.That(socket.MaxConcurrentSends).IsEqualTo(1);
    }

    [Test]
    public async Task WriteAsync_Should_Send_A_Framed_Binary_Input_Carrying_The_Attached_Viewport()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await session.WriteAsync(PersistentPtyFrameData.Output("ls\n"));

        await Assert.That(socket.SentMessageTypes.Single()).IsEqualTo(WebSocketMessageType.Binary);
        await Assert.That(socket.SentMessages.Single())
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 80, 24, PersistentPtyFrameData.Output("ls\n")));
    }

    [Test]
    public async Task WriteAsync_Should_Frame_At_The_Viewport_The_Server_Reported()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedWideViewportJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await session.WriteAsync(PersistentPtyFrameData.Output("ls\n"));

        // The attach is the only place the viewport comes from before a resize, and this terminal
        // is not at the server's default: a write that framed 80x24 anyway would resize the
        // caller's terminal on the very first keystroke.
        await Assert.That(socket.SentMessages.Single())
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 132, 50, PersistentPtyFrameData.Output("ls\n")));
    }

    [Test]
    public async Task WriteAsync_Should_Throw_After_Dispose()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        await session.DisposeAsync();

        _ = await Assert.That(async () => await session.WriteAsync(PersistentPtyFrameData.Output("late")))
            .Throws<ObjectDisposedException>();
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
    public async Task SubmitAsync_Should_Send_A_Framed_Line_Terminated_By_Enter()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await session.SubmitAsync(SubmittedLine);

        await Assert.That(socket.SentMessageTypes.Single()).IsEqualTo(WebSocketMessageType.Binary);
        await Assert.That(socket.SentMessages.Single())
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 80, 24, PersistentPtyFrameData.Output(SubmittedLine + Enter)));
    }

    [Test]
    public async Task SubmitAsync_Should_Frame_At_The_Viewport_A_Resize_Recorded()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await session.ResizeAsync(100, 30);
        await session.SubmitAsync(SubmittedLine);

        await Assert.That(socket.SentMessages[1])
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 100, 30, PersistentPtyFrameData.Output(SubmittedLine + Enter)));
    }

    [Test]
    public async Task SubmitAsync_Should_Send_A_Bare_Enter_For_An_Empty_Line()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await session.SubmitAsync(string.Empty);

        await Assert.That(socket.SentMessages.Single())
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 80, 24, PersistentPtyFrameData.Output(Enter)));
    }

    [Test]
    public async Task SubmitAsync_Should_Refuse_A_Null_Line()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        _ = await Assert.That(async () => await session.SubmitAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    [MethodDataSource(nameof(LinesCarryingABreak))]
    public async Task SubmitAsync_Should_Refuse_A_Line_Carrying_A_Break(string line)
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => await session.SubmitAsync(line)).Throws<ArgumentException>();

        // A submit is one Enter: an embedded break would run a command the caller never wrote.
        await Assert.That(failure!.ParamName).IsEqualTo("line");
        await Assert.That(socket.SentMessages).IsEmpty();
    }

    [Test]
    public async Task SubmitAsync_Should_Throw_After_Dispose()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        await session.DisposeAsync();

        _ = await Assert.That(async () => await session.SubmitAsync(SubmittedLine)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task SubmitAsync_Should_Report_A_Failed_Send_As_A_Transport_Failure()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .FailingNextSendWith(new WebSocketException("the connection dropped"));
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        var failure = await Assert.That(async () => await session.SubmitAsync(SubmittedLine))
            .Throws<OpenCodeTransportException>();

        await Assert.That(failure!.InnerException).IsTypeOf<WebSocketException>();
        await Assert.That(socket.SentMessages).IsEmpty();
    }

    [Test]
    public async Task ResizeAsync_Should_Send_A_Control_Frame_And_Track_The_Viewport()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        await session.ResizeAsync(100, 30);
        await session.WriteAsync(PersistentPtyFrameData.Output("ls\n"));

        await Assert.That(socket.SentMessages[0]).IsEquivalentTo(PersistentPtyFrameData.Framed(0, 100, 30, []));
        await Assert.That(socket.SentMessages[1])
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 100, 30, PersistentPtyFrameData.Output("ls\n")));
    }

    [Test]
    public async Task ResizeAsync_Should_Refuse_A_Zero_Or_Oversized_Dimension()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        _ = await Assert.That(async () => await session.ResizeAsync(0, 24)).Throws<ArgumentOutOfRangeException>();
        _ = await Assert.That(async () => await session.ResizeAsync(80, 0)).Throws<ArgumentOutOfRangeException>();
        _ = await Assert.That(async () => await session.ResizeAsync(65_536, 24)).Throws<ArgumentOutOfRangeException>();
        _ = await Assert.That(async () => await session.ResizeAsync(80, 65_536)).Throws<ArgumentOutOfRangeException>();

        // A refused resize never reaches the wire, so it cannot move the viewport later writes carry.
        await Assert.That(socket.SentMessages).IsEmpty();
    }

    [Test]
    public async Task ResizeAsync_Should_End_The_Connection_When_The_Control_Send_Fails()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .FailingNextSendWith(new WebSocketException("the connection dropped"));
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);

        _ = await Assert.That(async () => await session.ResizeAsync(100, 30)).Throws<OpenCodeTransportException>();
        _ = await Assert.That(async () => await session.WriteAsync(PersistentPtyFrameData.Output("ls\n")))
            .Throws<OpenCodeTransportException>();
        await Assert.That(socket.SentMessages).IsEmpty();
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ResizeAsync_Should_Preserve_The_Viewport_When_Canceled_Before_Socket_Entry()
    {
        var socket = new ScriptedTerminalWebSocket().Text(PersistentPtyFrameData.AttachedJson);
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert.That(async () => await session.ResizeAsync(100, 30, cancellation.Token))
            .Throws<OperationCanceledException>();
        await session.WriteAsync(PersistentPtyFrameData.Output("ls\n"));

        // No control entered the socket, so the still-live attachment retains its original size.
        await Assert.That(socket.SentMessages.Single())
            .IsEquivalentTo(PersistentPtyFrameData.Framed(1, 80, 24, PersistentPtyFrameData.Output("ls\n")));
    }

    [Test]
    public async Task ReadAsync_Should_Refuse_A_Second_Concurrent_Enumeration()
    {
        var socket = new ScriptedTerminalWebSocket()
            .Text(PersistentPtyFrameData.AttachedJson)
            .Binary(PersistentPtyFrameData.Output("live"))
            .Parking();
        await using var session = await PersistentPtySession.AttachAsync(socket, PtyId, CancellationToken.None);
        var first = session.ReadAsync().GetAsyncEnumerator(CancellationToken.None);
        await using var enumeration = first.ConfigureAwait(false);
        await Assert.That(await first.MoveNextAsync()).IsTrue();

        var second = session.ReadAsync().GetAsyncEnumerator(CancellationToken.None);

        _ = await Assert.That(async () => _ = await second.MoveNextAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Session_Mock_Seam_Should_Stay_Overridable()
    {
        await using var session = new MockPersistentPtySession();

        var frames = await ReadAllAsync(session);

        await Assert.That(((PersistentPtyOutputFrame)frames[0]).Data.ToArray())
            .IsEquivalentTo(PersistentPtyFrameData.Output("mocked"));
        await Assert.That(((PersistentPtyTitleChangedFrame)frames[1]).Title).IsEqualTo("vim");
    }

    [Test]
    public async Task Session_Mock_Seam_Should_Fail_Instructively_Without_An_Override()
    {
        await using var session = new UnoverriddenPersistentPtySession();

        var failure = await Assert.That(async () => _ = await ReadAllAsync(session)).Throws<InvalidOperationException>();

        await Assert.That(failure!.Message).Contains("mocking constructor");
    }

    /// <summary>
    /// The mocking seam is only usable if the frames an override yields can be built from outside
    /// this assembly. This test project is a friend, so the compiler cannot tell an internal
    /// constructor from a public one - reflection is what pins the accessibility an external
    /// consumer actually depends on.
    /// </summary>
    [Test]
    public async Task Frame_Constructors_Should_Be_Reachable_Without_Friend_Access()
    {
        (Type Frame, Type[] Parameters)[] frames =
        [
            (typeof(PersistentPtyAttachedFrame), [typeof(PersistentPtyAttachment)]),
            (typeof(PersistentPtyOutputFrame), [typeof(ReadOnlyMemory<byte>)]),
            (typeof(PersistentPtyReplayCompleteFrame), [typeof(long)]),
            (typeof(PersistentPtyResizedFrame), [typeof(int), typeof(int), typeof(long), typeof(ReadOnlyMemory<byte>)]),
            (typeof(PersistentPtyExitedFrame), [typeof(int?), typeof(long)]),
            (typeof(PersistentPtyControllerChangedFrame), [typeof(string), typeof(long)]),
            (typeof(PersistentPtyTitleChangedFrame), [typeof(string)]),
            (typeof(PersistentPtyForegroundProcessChangedFrame), [typeof(string)]),
            (typeof(PersistentPtyUnknownFrame), [typeof(string), typeof(JsonElement)]),
        ];

        // The unreachable frames are named rather than folded into one boolean: a failure has to
        // say which of the nine lost its public constructor, not merely that one of them did.
        var unreachable = frames
            .Where(frame => frame.Frame.GetConstructor(frame.Parameters)?.IsPublic is not true)
            .Select(static frame => frame.Frame.Name)
            .ToArray();

        await Assert.That(unreachable).IsEmpty();
    }

    private static async Task<List<PersistentPtyFrame>> ReadAllAsync(PersistentPtySession session)
    {
        var frames = new List<PersistentPtyFrame>();
        await ReadIntoAsync(session, frames);
        return frames;
    }

    private static async Task ReadIntoAsync(PersistentPtySession session, List<PersistentPtyFrame> frames)
    {
        await foreach (var frame in session.ReadAsync())
        {
            frames.Add(frame);
        }
    }

    private sealed class MockPersistentPtySession : PersistentPtySession
    {
        public override IAsyncEnumerable<PersistentPtyFrame> ReadAsync(CancellationToken cancellationToken = default) =>
            Mocked();

        /// <summary>
        /// Builds frames through their public doors, exactly as a consumer outside this assembly
        /// would write the same override.
        /// </summary>
        private static async IAsyncEnumerable<PersistentPtyFrame> Mocked()
        {
            await Task.Yield();
            yield return new PersistentPtyOutputFrame(PersistentPtyFrameData.Output("mocked"));
            yield return new PersistentPtyTitleChangedFrame("vim");
        }
    }

    private sealed class UnoverriddenPersistentPtySession : PersistentPtySession
    {
    }
}
