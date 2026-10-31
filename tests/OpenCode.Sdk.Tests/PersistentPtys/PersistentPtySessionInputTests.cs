using System.Net.WebSockets;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public sealed class PersistentPtySessionInputTests
{
    private const string MultibyteInput = "ş€😀";

    [Test]
    public async Task WriteAsync_Should_Copy_The_Input_Now_And_Send_Only_The_Frame_With_The_Current_Viewport()
    {
        var pool = new TrackingByteArrayPool(spareCapacity: 64);
        using var socket = new ScriptedTerminalWebSocket().GatingSends();
        await using var core = new TerminalSocketCore<PersistentPtyFrame>(socket, PersistentPtyFrameDecoder.Instance,
            PersistentPtyClosePolicy.Instance, typeof(PersistentPtySession), new ManualTimeProvider(), pool);
        var attachment = ((PersistentPtyAttachedFrame)PersistentPtyFrameDecoder.Instance.Decode(
            WebSocketMessageType.Text, Encoding.UTF8.GetBytes(PersistentPtyFrameData.AttachedJson),
            Encoding.UTF8.GetByteCount(PersistentPtyFrameData.AttachedJson))).Attachment;
        await using var session = new PersistentPtySession(core, attachment);

        var input = Enumerable.Repeat((byte)0x61, 100).ToArray();
        var first = session.WriteAsync(input);
        await socket.SendEntered;
        var resize = session.ResizeAsync(132, 50);
        var unicode = Encoding.UTF8.GetBytes(MultibyteInput);
        var write = session.WriteAsync(unicode);
        unicode[0] = 0;
        socket.ReleaseSends();
        await Task.WhenAll(first, resize, write);
        await Assert.That(socket.SentMessages[0]).IsEquivalentTo(new byte[] { 1, 0, 80, 0, 24 }.Concat(input), CollectionOrdering.Matching);
        await Assert.That(pool.RentCount).IsEqualTo(4);
        await Assert.That(pool.ReturnCount).IsEqualTo(3);
        await Assert.That(pool.OutstandingCount).IsEqualTo(1);
        await session.SubmitAsync(MultibyteInput);
        await Assert.That(socket.SentMessages[1]).IsEquivalentTo(new byte[] { 0, 0, 132, 0, 50 }, CollectionOrdering.Matching);
        await Assert.That(socket.SentMessages[2]).IsEquivalentTo(new byte[] { 1, 0, 132, 0, 50 }.Concat(Encoding.UTF8.GetBytes(MultibyteInput)), CollectionOrdering.Matching);
        await Assert.That(socket.SentMessages[3]).IsEquivalentTo(new byte[] { 1, 0, 132, 0, 50 }.Concat(Encoding.UTF8.GetBytes(MultibyteInput + "\r")), CollectionOrdering.Matching);
        await Assert.That(socket.SentMessageTypes).IsEquivalentTo(Enumerable.Repeat(WebSocketMessageType.Binary, 4), CollectionOrdering.Matching);
        await Assert.That(pool.RequestedLengths).IsEquivalentTo([TerminalSocketBounds.ReceiveBufferSize, 105, 5, 14, 15], CollectionOrdering.Matching);
        await Assert.That(pool.RentCount).IsEqualTo(5);
        await Assert.That(pool.ReturnCount).IsEqualTo(4);
        await Assert.That(pool.OutstandingCount).IsEqualTo(1);
    }
}
