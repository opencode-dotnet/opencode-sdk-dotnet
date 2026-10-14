using System.Net.WebSockets;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Terminal;

/// <summary>
/// The shared core's construction contract and the identity its refusals name. Both families'
/// session tests exercise the read, write, and close behaviour through their own doors; what only
/// the core can be asked directly is which collaborator it refuses to run without, and which type
/// a caller sees named when it writes to a session it has already disposed.
/// </summary>
public sealed class TerminalSocketCoreTests
{
    [Test]
    [Arguments(WebSocketCloseStatus.NormalClosure, "connection has ended")]
    [Arguments(WebSocketCloseStatus.InternalServerError, "1011")]
    public async Task SendAsync_Should_Wake_A_Queued_Call_With_The_Connections_Terminal_Failure(
        WebSocketCloseStatus status, string expectedMessage)
    {
        using var socket = new ScriptedTerminalWebSocket().Pausing()
            .Closing(status).GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        var first = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        await socket.SendEntered;
        var queued = core.SendAsync(new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None);
        socket.ReleaseReceives();
        OpenCodeTransportException? failure = null;
        try
        {
            await queued;
        }
        catch (OpenCodeTransportException exception)
        {
            failure = exception;
        }
        finally
        {
            socket.ReleaseSends();
            await first;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains(expectedMessage);
        await Assert.That(socket.SentMessages.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SendAsync_Should_Preserve_The_First_Terminal_Outcome_After_A_Later_Send_Cancellation()
    {
        using var socket = new ScriptedTerminalWebSocket().Pausing()
            .Closing(WebSocketCloseStatus.InternalServerError).GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        using var cancellation = new CancellationTokenSource();
        var pending = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, cancellation.Token);
        await socket.SendEntered;
        socket.ReleaseReceives();
        await using var reader = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        var firstFailure = await Assert.That(async () => _ = await reader.MoveNextAsync()).Throws<OpenCodeTransportException>();
        await cancellation.CancelOnWorkerAsync();
        OperationCanceledException? canceled = null;
        try
        {
            await pending;
        }
        catch (OperationCanceledException exception)
        {
            canceled = exception;
        }

        await Assert.That(canceled).IsNotNull();
        var laterFailure = await Assert.That(async () => await core.SendAsync(
            new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None)).Throws<OpenCodeTransportException>();
        await Assert.That(laterFailure).IsSameReferenceAs(firstFailure);
    }

    [Test]
    public async Task SendAsync_Should_End_The_Attachment_After_Physical_Caller_Cancellation()
    {
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        using var cancellation = new CancellationTokenSource();
        var pending = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, cancellation.Token);
        await socket.SendEntered;
        await cancellation.CancelOnWorkerAsync();
        OperationCanceledException? failure = null;
        try
        {
            await pending;
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
        }

        await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
        _ = await Assert.That(async () => await core.SendAsync(
            new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None)).Throws<OpenCodeTransportException>();
        await Assert.That(socket.SentMessages.Count).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_A_Fragmented_Message_Across_Consumer_Cancellation()
    {
        using var socket = new ScriptedTerminalWebSocket()
            .TextFragment("first ").Pausing().Text("second").Closing(WebSocketCloseStatus.NormalClosure);
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        using var cancellation = new CancellationTokenSource();
        await using (var reader = core.ReadAsync(cancellation.Token).GetAsyncEnumerator())
        {
            var pending = reader.MoveNextAsync();
            await socket.Paused;
            await cancellation.CancelOnWorkerAsync();
            _ = await Assert.That(async () => _ = await pending).Throws<OperationCanceledException>();
        }

        await core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        socket.ReleaseReceives();
        await using var resumed = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await resumed.MoveNextAsync()).IsTrue();
        await Assert.That(((PtyOutputFrame)resumed.Current).Text).IsEqualTo("first second");
        await Assert.That(await resumed.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task ReadAsync_Should_Leave_Queued_Data_And_The_Terminal_Error_When_Already_Canceled()
    {
        using var socket = new ScriptedTerminalWebSocket().Text("prefix").Closing(WebSocketCloseStatus.InternalServerError);
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();
        await using (var reader = core.ReadAsync(cancellation.Token).GetAsyncEnumerator())
        {
            _ = await Assert.That(async () => _ = await reader.MoveNextAsync()).Throws<OperationCanceledException>();
        }

        await using var resumed = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await resumed.MoveNextAsync()).IsTrue();
        await Assert.That(((PtyOutputFrame)resumed.Current).Text).IsEqualTo("prefix");
        _ = await Assert.That(async () => _ = await resumed.MoveNextAsync()).Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ReadAsync_Should_Preserve_A_Connection_Failure_Across_Readers()
    {
        using var socket = new ScriptedTerminalWebSocket().Text("prefix").Closing(WebSocketCloseStatus.InternalServerError);
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        await using (var first = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            await Assert.That(await first.MoveNextAsync()).IsTrue();
            await Assert.That(((PtyOutputFrame)first.Current).Text).IsEqualTo("prefix");
            _ = await Assert.That(async () => _ = await first.MoveNextAsync()).Throws<OpenCodeTransportException>();
        }

        await using var second = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        var failure = await Assert.That(async () => _ = await second.MoveNextAsync()).Throws<OpenCodeTransportException>();
        await Assert.That(failure!.Message).Contains("1011");
    }

    [Test]
    public async Task ReadAsync_Should_Keep_Normal_Completion_After_The_Queued_Prefix()
    {
        using var socket = new ScriptedTerminalWebSocket().Text("prefix").Closing(WebSocketCloseStatus.NormalClosure);
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        await using (var first = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            await Assert.That(await first.MoveNextAsync()).IsTrue();
            await Assert.That(await first.MoveNextAsync()).IsFalse();
        }

        await using var second = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await second.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_Should_Not_Require_A_Suspended_Consumer_To_Resume()
    {
        using var socket = new ScriptedTerminalWebSocket().Text("prefix").Text("unread").Parking();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        await using var first = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await first.MoveNextAsync()).IsTrue();

        await core.DisposeAsync();

        await using var afterDisposal = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await afterDisposal.MoveNextAsync()).IsFalse();
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_Should_Share_In_Progress_Cleanup()
    {
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        var send = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        await socket.SendEntered;
        var firstDisposal = core.DisposeAsync();
        var secondDisposal = core.DisposeAsync();
        var first = firstDisposal.AsTask();
        var second = secondDisposal.AsTask();
        try
        {
            await Assert.That(second.IsCompleted).IsFalse();
        }
        finally
        {
            socket.ReleaseSends();
        }

        await Task.WhenAll(first, second, send);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task SendAsync_Should_Expire_A_Queued_Call_Without_Closing_The_Socket()
    {
        using var firstDeadline = new CancellationTokenSource();
        using var queuedDeadline = new CancellationTokenSource();
        using var nextDeadline = new CancellationTokenSource();
        var timers = Substitute.For<ITerminalDeadlineFactory>();
        _ = timers.Create(Arg.Any<TimeSpan>()).Returns(firstDeadline, queuedDeadline, nextDeadline);
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession))
        {
            DeadlineFactory = timers,
        };
        var first = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        await socket.SendEntered;
        var queued = core.SendAsync(new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None);
        await queuedDeadline.CancelOnWorkerAsync();
        socket.ReleaseSends();
        await first;

        OpenCodeTransportException? failure = null;
        try
        {
            await queued;
        }
        catch (OpenCodeTransportException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.InnerException).IsTypeOf<TimeoutException>();
        await Assert.That(socket.SentMessages.Count).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(0);
        await core.SendAsync(new ArraySegment<byte>([0x63]), WebSocketMessageType.Text, CancellationToken.None);
        await Assert.That(socket.SentMessages.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SendAsync_Should_End_The_Attachment_When_A_Physical_Send_Expires()
    {
        using var deadline = new CancellationTokenSource();
        var timers = Substitute.For<ITerminalDeadlineFactory>();
        _ = timers.Create(Arg.Any<TimeSpan>()).Returns(deadline);
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession))
        {
            DeadlineFactory = timers,
        };
        var send = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        await socket.SendEntered;
        await deadline.CancelOnWorkerAsync();
        socket.ReleaseSends();

        OpenCodeTransportException? failure = null;
        try
        {
            await send;
        }
        catch (OpenCodeTransportException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.InnerException).IsTypeOf<TimeoutException>();
        _ = await Assert.That(async () => await core.SendAsync(
            new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None))
            .Throws<OpenCodeTransportException>();
        await Assert.That(socket.SentMessages.Count).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Socket()
    {
        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket: null!, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession)));

        await Assert.That(failure.ParamName).IsEqualTo("socket");
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Decoder()
    {
        using var socket = new ScriptedTerminalWebSocket();

        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket, decoder: null!, PtyClosePolicy.Instance, typeof(PtySession)));

        await Assert.That(failure.ParamName).IsEqualTo("decoder");
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Close_Policy()
    {
        using var socket = new ScriptedTerminalWebSocket();

        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, closePolicy: null!, typeof(PtySession)));

        await Assert.That(failure.ParamName).IsEqualTo("closePolicy");
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Owner()
    {
        using var socket = new ScriptedTerminalWebSocket();

        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, owner: null!));

        await Assert.That(failure.ParamName).IsEqualTo("owner");
    }

    [Test]
    public async Task SendAsync_Should_Name_The_Owning_Door_Once_Disposed()
    {
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession));
        await core.DisposeAsync();

        var failure = await Assert
            .That(async () => await core.SendAsync(
                new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None))
            .Throws<ObjectDisposedException>();

        // The name is deliberately the door the caller disposed, not this internal core: a
        // consumer holds a PtySession and never learns that a TerminalSocketCore exists.
        await Assert.That(failure!.ObjectName).IsEqualTo(typeof(PtySession).FullName);
    }
}
