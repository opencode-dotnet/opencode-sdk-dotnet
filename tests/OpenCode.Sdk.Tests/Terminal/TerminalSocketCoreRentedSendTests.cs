using System.Net.WebSockets;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Terminal;

public sealed class TerminalSocketCoreRentedSendTests
{
    private const int PayloadLength = 7;

    [Test]
    [Arguments("success")]
    [Arguments("prepare")]
    [Arguments("sent")]
    [Arguments("socket")]
    public async Task SendAsync_Should_Return_And_Clear_The_Rent_When_The_Send_Settles(string path)
    {
        var pool = new TrackingByteArrayPool(spareCapacity: 64);
        using var socket = new ScriptedTerminalWebSocket();
        var injected = path is "socket" ? (Exception)new IOException("Injected socket failure.") : new FormatException("Injected send step failure.");
        if (path is "socket")
        {
            _ = socket.FailingNextSendWith(injected);
        }

        await using var core = CreateCore(socket, pool, new ManualTimeProvider());
        var buffer = pool.Rent(PayloadLength);
        var actions = new TerminalSendActions(
            Prepare: path is "prepare" ? _ => throw injected : null,
            OnSent: path is "sent" ? () => throw injected : null, PayloadRented: true);
        var send = core.SendAsync(() => new ArraySegment<byte>(buffer, 0, PayloadLength), WebSocketMessageType.Text, actions, CancellationToken.None);
        if (path is "success")
        {
            await send;
        }
        else if (path is "socket")
        {
            var failure = await Assert.That(send.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
            await Assert.That(failure!.InnerException).IsSameReferenceAs(injected);
            await core.DisposeAsync();
        }
        else
        {
            var failure = await Assert.That(send.WaitAsync(HangGuard.Bound)).Throws<FormatException>();
            await Assert.That(failure).IsSameReferenceAs(injected);
        }

        await AssertCountsAsync(pool, 2, path is "socket" ? 2 : 1, path is "socket" ? 0 : 1);
        await Assert.That(pool.RequestedLengths).IsEquivalentTo([TerminalSocketBounds.ReceiveBufferSize, PayloadLength], CollectionOrdering.Matching);
        await Assert.That(buffer).IsEquivalentTo(new byte[buffer.Length], CollectionOrdering.Matching);
        await core.DisposeAsync();
        await AssertCountsAsync(pool, 2, 2, 0);
    }

    [Test]
    [Arguments("timeout")]
    [Arguments("caller")]
    [Arguments("failure")]
    [Arguments("dispose")]
    public async Task SendAsync_Should_Return_A_Queued_Rent_Before_The_First_Send_Is_Released(string path)
    {
        var pool = new TrackingByteArrayPool(spareCapacity: 64);
        var time = new ManualTimeProvider();
        using var socket = new ScriptedTerminalWebSocket().Pausing().Closing(WebSocketCloseStatus.InternalServerError);
        await using var core = CreateCore(socket, pool, time);
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;
        var first = Task.Run(() => SendRented(core, pool, new TerminalSendActions(OnSent: () =>
        {
            _ = entered.TrySetResult(true);
            released = release.Wait(HangGuard.Bound);
        }, PayloadRented: true), CancellationToken.None));
        var disposal = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(HangGuard.Bound);
            byte[] buffer = [];
            var queued = core.SendAsync(() =>
            {
                buffer = pool.Rent(PayloadLength);
                return new ArraySegment<byte>(buffer, 0, PayloadLength);
            }, WebSocketMessageType.Text, TerminalSendActions.Rented, cancellation.Token);
            if (path is "timeout")
            {
                await time.WaitForArmedAsync(core.SendTimeout, HangGuard.Bound, count: 2);
                time.Advance(core.SendTimeout + TimeSpan.FromTicks(1));
                var failure = await Assert.That(queued.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
                await Assert.That(failure!.Message).IsEqualTo("The opencode PTY WebSocket send timed out while waiting for send admission.");
                await Assert.That(failure.InnerException).IsTypeOf<TimeoutException>();
            }
            else if (path is "caller")
            {
                await cancellation.CancelOnWorkerAsync();
                var failure = await Assert.That(queued.WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
                await Assert.That(failure!.CancellationToken).IsEqualTo(cancellation.Token);
            }
            else if (path is "failure")
            {
                socket.ReleaseReceives();
                var failure = await Assert.That(queued.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
                await using var reader = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
                var readFailure = await Assert.That(async () => _ = await reader.MoveNextAsync()).Throws<OpenCodeTransportException>();
                await Assert.That(failure).IsSameReferenceAs(readFailure);
                await Assert.That(failure!.Message).Contains("1011");
            }
            else
            {
                disposal = core.DisposeAsync().AsTask();
                _ = await Assert.That(queued.WaitAsync(HangGuard.Bound)).Throws<ObjectDisposedException>();
            }

            await AssertCountsAsync(pool, 3, path is "failure" ? 2 : 1, path is "failure" ? 1 : 2);
            await Assert.That(pool.RequestedLengths).IsEquivalentTo([TerminalSocketBounds.ReceiveBufferSize, PayloadLength, PayloadLength], CollectionOrdering.Matching);
            await Assert.That(buffer).IsEquivalentTo(new byte[buffer.Length], CollectionOrdering.Matching);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(HangGuard.Bound);
            await disposal.WaitAsync(HangGuard.Bound);
        }

        await Assert.That(released).IsTrue();
        await AssertCountsAsync(pool, 3, path is "failure" or "dispose" ? 3 : 2, path is "failure" or "dispose" ? 0 : 1);
    }

    [Test]
    public async Task SendAsync_Should_Keep_The_Rent_Until_A_Cancellation_Ignoring_Write_Settles()
    {
        var pool = new TrackingByteArrayPool();
        var time = new ManualTimeProvider();
        using var receiver = new ScriptedTerminalWebSocket();
        using var socket = Substitute.For<ITerminalWebSocket>();
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => receiver.ReceiveAsync(call.Arg<ArraySegment<byte>>(), call.Arg<CancellationToken>()));
        var physical = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = socket.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<CancellationToken>()).Returns(physical.Task);
        await using var core = CreateCore(socket, pool, time);
        var pending = SendRented(core, pool, TerminalSendActions.Rented, CancellationToken.None);
        var injected = new IOException("Injected outstanding write failure.");
        try
        {
            await time.WaitForArmedAsync(core.SendTimeout, HangGuard.Bound);
            time.Advance(core.SendTimeout);
            await Assert.That(pending.IsCompleted).IsFalse();
            await AssertCountsAsync(pool, 2, 0, 2);
        }
        finally
        {
            physical.SetException(injected);
            var failure = await Assert.That(pending.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
            await Assert.That(failure!.InnerException).IsTypeOf<TimeoutException>();
            await Assert.That(failure.InnerException!.InnerException).IsSameReferenceAs(injected);
        }

        await AssertCountsAsync(pool, 2, 1, 1);
        await core.DisposeAsync();
        await AssertCountsAsync(pool, 2, 2, 0);
    }

    [Test]
    public async Task SendAsync_Should_Not_Return_Caller_Owned_Arrays_Or_Create_A_Payload_After_Disposal()
    {
        var pool = new TrackingByteArrayPool();
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = CreateCore(socket, pool, new ManualTimeProvider());
        var payload = new ArraySegment<byte>([0x61]);
        await core.SendAsync(payload, WebSocketMessageType.Text, CancellationToken.None);
        await core.SendAsync(() => payload, WebSocketMessageType.Text, null, CancellationToken.None);
        await AssertCountsAsync(pool, 1, 0, 1);
        await core.DisposeAsync();
        var called = false;
        _ = await Assert.That(async () => await core.SendAsync(() =>
        {
            called = true;
            return new ArraySegment<byte>(pool.Rent(PayloadLength), 0, PayloadLength);
        }, WebSocketMessageType.Text, TerminalSendActions.Rented, CancellationToken.None)).Throws<ObjectDisposedException>();
        await Assert.That(called).IsFalse();
        await Assert.That(pool.RequestedLengths).IsEquivalentTo([TerminalSocketBounds.ReceiveBufferSize], CollectionOrdering.Matching);
        await AssertCountsAsync(pool, 1, 1, 0);
    }

    [Test]
    public async Task SendAsync_Should_Return_And_Clear_The_Rent_When_Refused_Behind_The_Gate()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var released = false;
        var pool = new TrackingByteArrayPool(spareCapacity: 64, onReturn: array =>
        {
            if (array.Length >= TerminalSocketBounds.ReceiveBufferSize)
            {
                entered.Set();
                released = release.Wait(HangGuard.Bound);
            }
        });
        using var socket = new ScriptedTerminalWebSocket().Pausing().Closing(WebSocketCloseStatus.InternalServerError);
        await using var core = CreateCore(socket, pool, new ManualTimeProvider());
        byte[] buffer = [];
        var outcomeSet = false;
        try
        {
            var send = core.SendAsync(() =>
            {
                socket.ReleaseReceives();
                outcomeSet = entered.Wait(HangGuard.Bound);
                buffer = pool.Rent(PayloadLength);
                return new ArraySegment<byte>(buffer, 0, PayloadLength);
            }, WebSocketMessageType.Text, TerminalSendActions.Rented, CancellationToken.None);
            var failure = await Assert.That(send.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
            await Assert.That(outcomeSet).IsTrue();
            await Assert.That(failure!.Message).Contains("1011");
            await Assert.That(socket.SentMessages).IsEmpty();
            await AssertCountsAsync(pool, 2, 1, 1);
            await Assert.That(buffer).IsEquivalentTo(new byte[buffer.Length], CollectionOrdering.Matching);
        }
        finally
        {
            release.Set();
            await core.DisposeAsync().AsTask().WaitAsync(HangGuard.Bound);
        }

        await Assert.That(released).IsTrue();
        await AssertCountsAsync(pool, 2, 2, 0);
    }

    private static TerminalSocketCore<PtyFrame> CreateCore(ITerminalWebSocket socket, TrackingByteArrayPool pool, ManualTimeProvider time) =>
        new(socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), time, pool);

    private static Task SendRented(TerminalSocketCore<PtyFrame> core, TrackingByteArrayPool pool, TerminalSendActions actions, CancellationToken token) =>
        core.SendAsync(() => new ArraySegment<byte>(pool.Rent(PayloadLength), 0, PayloadLength), WebSocketMessageType.Text, actions, token);

    private static async Task AssertCountsAsync(TrackingByteArrayPool pool, int rents, int returns, int outstanding)
    {
        // Counts include the receiver's buffer whenever a core is present, not just send frames.
        await Assert.That(pool.RentCount).IsEqualTo(rents);
        await Assert.That(pool.ReturnCount).IsEqualTo(returns);
        await Assert.That(pool.OutstandingCount).IsEqualTo(outstanding);
    }
}
