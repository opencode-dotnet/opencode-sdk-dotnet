using System.Net.WebSockets;
using NSubstitute;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Terminal;

public sealed class TerminalSendDeadlineTests
{
    [Test]
    public async Task Map_Should_Preserve_A_Connection_Outcome_Recorded_Before_Expiry()
    {
        using var timer = new CancellationTokenSource();
        var timers = CreateTimers(timer);
        TerminalConnectionOutcome? recorded = null;
        using var deadline = new TerminalSendDeadline(
            typeof(PtySession), TerminalSocketBounds.DefaultSendTimeout, timers, () => recorded, CancellationToken.None);
        var reset = new OpenCodeTransportException("The connection reset before expiry.");
        recorded = new TerminalConnectionOutcome(reset);

        await timer.CancelOnWorkerAsync();
        var failure = deadline.Map(new OperationCanceledException(deadline.Token), queued: true, disposed: false, recorded);

        await Assert.That(failure).IsSameReferenceAs(reset);
    }

    [Test]
    public async Task Map_Should_Preserve_The_Callers_Token_After_Expiry()
    {
        using var timer = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var timers = CreateTimers(timer);
        using var deadline = new TerminalSendDeadline(
            typeof(PtySession), TerminalSocketBounds.DefaultSendTimeout, timers, static () => null, caller.Token);
        await timer.CancelOnWorkerAsync();
        await caller.CancelOnWorkerAsync();
        var interrupted = new OperationCanceledException(deadline.Token);

        var failure = deadline.Map(interrupted, queued: true, disposed: false, outcome: null);

        await Assert.That(failure).IsTypeOf<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)failure).CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(failure.InnerException).IsSameReferenceAs(interrupted);
    }

    [Test]
    public async Task Map_Should_Preserve_A_Reset_That_Precedes_Deadline_Interruption()
    {
        using var timer = new CancellationTokenSource();
        var timers = CreateTimers(timer);
        using var socket = Substitute.For<ITerminalWebSocket>();
        var send = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = socket.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<CancellationToken>())
            .Returns(send.Task);
        using var deadline = new TerminalSendDeadline(
            typeof(PtySession), TerminalSocketBounds.DefaultSendTimeout, timers, static () => null, CancellationToken.None);
        var reset = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        send.SetException(reset);
        WebSocketException? original = null;
        try
        {
            await deadline.SendAsync(socket, new ArraySegment<byte>([0x61]), WebSocketMessageType.Text);
        }
        catch (WebSocketException exception)
        {
            original = exception;
        }

        await timer.CancelOnWorkerAsync();
        await Assert.That(original).IsSameReferenceAs(reset);
        var failure = deadline.Map(original!, queued: false, disposed: false, outcome: null);

        await Assert.That(failure).IsTypeOf<OpenCodeTransportException>();
        await Assert.That(failure.InnerException).IsSameReferenceAs(reset);
    }

    internal static ITerminalDeadlineFactory CreateTimers(CancellationTokenSource timer)
    {
        var timers = Substitute.For<ITerminalDeadlineFactory>();
        _ = timers.Create(Arg.Any<TimeSpan>()).Returns(timer);
        return timers;
    }
}
