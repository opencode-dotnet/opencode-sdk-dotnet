using System.Net.WebSockets;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

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
    public async Task ReadAsync_Should_Limit_An_Oversized_Rent_And_Return_It_After_Normal_Close()
    {
        var pool = new TrackingByteArrayPool(spareCapacity: 64);
        using var socket = Substitute.For<ITerminalWebSocket>();
        var receivedCount = 0;
        _ = socket.CloseStatus.Returns(WebSocketCloseStatus.NormalClosure);
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            receivedCount = call.Arg<ArraySegment<byte>>().Count;
            return Task.FromResult(new PtyReceiveResult(WebSocketMessageType.Close, 0, true));
        });
        await using var core = CreateCore(socket, pool);

        await core.DisposeAsync();

        await Assert.That(receivedCount).IsEqualTo(TerminalSocketBounds.ReceiveBufferSize);
        await Assert.That(pool.RequestedLengths).IsEquivalentTo([TerminalSocketBounds.ReceiveBufferSize], CollectionOrdering.Matching);
        await AssertReturnedOnceAsync(pool);
    }

    [Test]
    public async Task DisposeAsync_Should_Return_The_Receive_Buffer_When_Receive_Faults()
    {
        var pool = new TrackingByteArrayPool();
        using var socket = Substitute.For<ITerminalWebSocket>();
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PtyReceiveResult>(new WebSocketException(WebSocketError.ConnectionClosedPrematurely)));
        await using var core = CreateCore(socket, pool);

        await core.DisposeAsync();

        await AssertReturnedOnceAsync(pool);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task DisposeAsync_Should_Return_The_Receive_Buffer_When_A_Collaborator_Throws(bool decoderThrows)
    {
        var pool = new TrackingByteArrayPool();
        var injected = new FormatException("Injected terminal collaborator failure.");
        using var socket = Substitute.For<ITerminalWebSocket>();
        var decoder = Substitute.For<ITerminalFrameDecoder<PtyFrame>>();
        _ = decoder.Decode(Arg.Any<WebSocketMessageType>(), Arg.Any<byte[]>(), Arg.Any<int>()).Returns(_ => throw injected);
        var closePolicy = Substitute.For<ITerminalClosePolicy>();
        _ = closePolicy.Map(Arg.Any<WebSocketCloseStatus?>(), Arg.Any<string?>()).Returns(_ => throw injected);
        var messageType = decoderThrows ? WebSocketMessageType.Text : WebSocketMessageType.Close;
        _ = socket.CloseStatus.Returns(WebSocketCloseStatus.NormalClosure);
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PtyReceiveResult(messageType, 0, true)));
        FormatException? failure = null;
        try
        {
            await using var core = new TerminalSocketCore<PtyFrame>(
                socket, decoderThrows ? decoder : PtyFrameDecoder.Instance, decoderThrows ? PtyClosePolicy.Instance : closePolicy,
                typeof(PtySession), new ManualTimeProvider(), pool);

            await core.DisposeAsync();
        }
        catch (FormatException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsSameReferenceAs(injected);
        await AssertReturnedOnceAsync(pool);
    }

    [Test]
    public async Task DisposeAsync_Should_Return_The_Receive_Buffer_When_It_Cancels_A_Parked_Receive()
    {
        var pool = new TrackingByteArrayPool();
        using var socket = Substitute.For<ITerminalWebSocket>();
        var receiveToken = CancellationToken.None;
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            receiveToken = call.Arg<CancellationToken>();
            var receive = new TaskCompletionSource<PtyReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = receiveToken.Register(() => receive.TrySetCanceled());
            return await receive.Task;
        });
        await using var core = CreateCore(socket, pool);

        await core.DisposeAsync();

        await Assert.That(receiveToken.IsCancellationRequested).IsTrue();
        await AssertReturnedOnceAsync(pool);
    }

    private static TerminalSocketCore<PtyFrame> CreateCore(ITerminalWebSocket socket, TrackingByteArrayPool pool) =>
        new(socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider(), pool);

    private static async Task AssertReturnedOnceAsync(TrackingByteArrayPool pool)
    {
        await Assert.That(pool.RentCount).IsEqualTo(1);
        await Assert.That(pool.ReturnCount).IsEqualTo(1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task SendAsync_Should_Preserve_A_Reset_Completed_Before_Deadline_Interruption()
    {
        var time = new ManualTimeProvider();
        using var socket = Substitute.For<ITerminalWebSocket>();
        var send = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reset = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var received = new TaskCompletionSource<PtyReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = token.Register(() => received.TrySetCanceled(token));
            return await received.Task;
        });
        _ = socket.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<CancellationToken>())
            .Returns(send.Task);
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), time);
        var entered = 0;
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var released = false;
        // ConfigureAwait(false) still flows ExecutionContext. Gate re-entry after the physical
        // task is final, so expiry precedes the core's continuation mapping its reset.
        var interruptOnResume = new AsyncLocal<bool>(change =>
        {
            if (change.ThreadContextChanged && change.CurrentValue && send.Task.IsCompleted &&
                Interlocked.CompareExchange(ref entered, 1, 0) is 0)
            {
                _ = resumed.TrySetResult(true);
                released = release.Wait(HangGuard.Bound);
            }
        });
        Task pending;
        interruptOnResume.Value = true;
        try
        {
            pending = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        }
        finally
        {
            interruptOnResume.Value = false;
        }

        try
        {
            send.SetException(reset);
            await resumed.Task.WaitAsync(HangGuard.Bound);
            await time.WaitForArmedAsync(core.SendTimeout, HangGuard.Bound);
            time.Advance(core.SendTimeout);
            release.Set();
            var failure = await Assert.That(async () =>
            {
                Task[] sends = [pending];
                await Task.WhenAll(sends).WaitAsync(HangGuard.Bound);
            })
                .Throws<OpenCodeTransportException>();

            await Assert.That(released).IsTrue();
            await Assert.That(failure!.InnerException).IsSameReferenceAs(reset);
        }
        finally
        {
            release.Set();
            _ = send.TrySetException(reset);
            await core.DisposeAsync().AsTask().WaitAsync(HangGuard.Bound);
        }
    }

    [Test]
    public async Task SendAsync_Should_Report_A_Timeout_When_The_Deadline_Abort_Reaches_Receive_First()
    {
        var time = new ManualTimeProvider();
        using var socket = Substitute.For<ITerminalWebSocket>();
        var receive = new TaskCompletionSource<PtyReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readFault = new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        var writeFault = new IOException("The deadline aborted the pending write.");
        _ = socket.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>()).Returns(receive.Task);
        var operationToken = CancellationToken.None;
        _ = socket.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<CancellationToken>())
            .Returns(send.Task).AndDoes(call =>
            {
                operationToken = call.Arg<CancellationToken>();
            });
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), time);
        var pending = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        using var abort = operationToken.Register(() => receive.TrySetException(readFault));
        OpenCodeTransportException? attachmentFailure = null;
        OpenCodeTransportException? sendFailure = null;
        await using var reader = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        try
        {
            await time.WaitForArmedAsync(core.SendTimeout, HangGuard.Bound);
            time.Advance(core.SendTimeout);
            attachmentFailure = await Assert.That(async () => _ = await reader.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound))
                .Throws<OpenCodeTransportException>();
        }
        finally
        {
            _ = receive.TrySetException(readFault);
            _ = send.TrySetException(writeFault);
            try
            {
                _ = await Assert.That(Task.WhenAll(receive.Task, send.Task).WaitAsync(HangGuard.Bound))
                    .Throws<WebSocketException>();
            }
            finally
            {
                sendFailure = await Assert.That(async () =>
            {
                Task[] sends = [pending];
                await Task.WhenAll(sends).WaitAsync(HangGuard.Bound);
            })
                    .Throws<OpenCodeTransportException>();
            }
        }

        await Assert.That(sendFailure!.InnerException).IsTypeOf<TimeoutException>();
        await Assert.That(sendFailure.InnerException!.InnerException).IsSameReferenceAs(writeFault);
        await Assert.That(attachmentFailure!.InnerException).IsSameReferenceAs(readFault);
        var laterFailure = await Assert.That(async () => await core.SendAsync(
            new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None))
            .Throws<OpenCodeTransportException>();
        await Assert.That(laterFailure).IsSameReferenceAs(attachmentFailure);
        await socket.Received(1).SendAsync(
            Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<CancellationToken>());
        socket.Received(1).Dispose();
    }

    [Test]
    [Arguments(WebSocketCloseStatus.NormalClosure, "connection has ended")]
    [Arguments(WebSocketCloseStatus.InternalServerError, "1011")]
    public async Task SendAsync_Should_Wake_A_Queued_Call_With_The_Connections_Terminal_Failure(
        WebSocketCloseStatus status, string expectedMessage)
    {
        using var socket = new ScriptedTerminalWebSocket().Pausing()
            .Closing(status).GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
        await using (var first = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator())
        {
            await Assert.That(await first.MoveNextAsync()).IsTrue();
            await Assert.That(await first.MoveNextAsync()).IsFalse();
        }

        await using var second = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await second.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_Should_Hard_Close_And_Join_Io_When_Graceful_Close_Fails()
    {
        using var socket = new ScriptedTerminalWebSocket().Parking()
            .FailingCloseWith(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
        await socket.Parked;
        await core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);

        await core.DisposeAsync();
        await Assert.That(socket.CloseOutputCalls).IsEqualTo(1);
        await Assert.That(socket.DisposeCalls).IsEqualTo(1);
        await Assert.That(socket.MaxConcurrentSends).IsEqualTo(1);
        await using var reader = core.ReadAsync(CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_Should_Not_Require_A_Suspended_Consumer_To_Resume()
    {
        using var socket = new ScriptedTerminalWebSocket().Text("prefix").Text("unread").Parking();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
        var time = new ManualTimeProvider();
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        using var transport = Substitute.For<ITerminalWebSocket>();
        var physicalSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordedSend = Task.CompletedTask;
        _ = transport.ReceiveAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => socket.ReceiveAsync(call.Arg<ArraySegment<byte>>(), call.Arg<CancellationToken>()));
        _ = transport.SendAsync(Arg.Any<ArraySegment<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<CancellationToken>())
            .Returns(physicalSend.Task).AndDoes(call =>
            {
                recordedSend = socket.SendAsync(
                    call.Arg<ArraySegment<byte>>(), call.Arg<WebSocketMessageType>(), call.Arg<CancellationToken>());
            });
        _ = transport.CloseOutputAsync(Arg.Any<CancellationToken>())
            .Returns(call => socket.CloseOutputAsync(call.Arg<CancellationToken>()));
        transport.When(value => value.Dispose()).Do(_ => socket.Dispose());
        await using var core = new TerminalSocketCore<PtyFrame>(
            transport, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), time);
        var resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var released = false;
        // Hold the first call in its own post-send step, before it releases the send gate, so only
        // the queued call can expire; the hold never throws, and the test thread asserts its result.
        var holdAfterSend = new TerminalSendActions(OnSent: () =>
        {
            _ = resumed.TrySetResult(true);
            released = release.Wait(HangGuard.Bound);
        });
        var first = core.SendAsync(() => new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, holdAfterSend, CancellationToken.None);

        await socket.SendEntered;
        var queued = core.SendAsync(new ArraySegment<byte>([0x62]), WebSocketMessageType.Text, CancellationToken.None);
        OpenCodeTransportException? failure = null;
        try
        {
            socket.ReleaseSends();
            await recordedSend.WaitAsync(HangGuard.Bound);
            physicalSend.SetResult(true);
            await resumed.Task.WaitAsync(HangGuard.Bound);
            await time.WaitForArmedAsync(core.SendTimeout, HangGuard.Bound, count: 2);
            time.Advance(core.SendTimeout);
            await queued;
        }
        catch (OpenCodeTransportException exception)
        {
            failure = exception;
        }
        finally
        {
            release.Set();
            socket.ReleaseSends();
            await recordedSend.WaitAsync(HangGuard.Bound);
            _ = physicalSend.TrySetResult(true);
            await first.WaitAsync(HangGuard.Bound);
        }

        await Assert.That(released).IsTrue();
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
        var time = new ManualTimeProvider();
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), time);
        var send = core.SendAsync(new ArraySegment<byte>([0x61]), WebSocketMessageType.Text, CancellationToken.None);
        await socket.SendEntered;
        await time.WaitForArmedAsync(core.SendTimeout, HangGuard.Bound);
        time.Advance(core.SendTimeout);
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
            socket: null!, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider()));

        await Assert.That(failure.ParamName).IsEqualTo("socket");
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Decoder()
    {
        using var socket = new ScriptedTerminalWebSocket();

        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket, decoder: null!, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider()));

        await Assert.That(failure.ParamName).IsEqualTo("decoder");
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Close_Policy()
    {
        using var socket = new ScriptedTerminalWebSocket();

        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, closePolicy: null!, typeof(PtySession), new ManualTimeProvider()));

        await Assert.That(failure.ParamName).IsEqualTo("closePolicy");
    }

    [Test]
    public async Task Constructor_Should_Refuse_A_Null_Owner()
    {
        using var socket = new ScriptedTerminalWebSocket();

        var failure = Assert.Throws<ArgumentNullException>(() => _ = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, owner: null!, new ManualTimeProvider()));

        await Assert.That(failure.ParamName).IsEqualTo("owner");
    }

    [Test]
    public async Task SendAsync_Should_Name_The_Owning_Door_Once_Disposed()
    {
        using var socket = new ScriptedTerminalWebSocket();
        await using var core = new TerminalSocketCore<PtyFrame>(
            socket, PtyFrameDecoder.Instance, PtyClosePolicy.Instance, typeof(PtySession), new ManualTimeProvider());
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
