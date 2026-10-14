using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Streaming;

public sealed class ServerSentEventFramerTests
{
    [Test]
    public async Task ReadAsync_Should_Frame_Each_Body_With_Fresh_State()
    {
        var framer = new ServerSentEventFramer();
        using var firstBody = new MemoryStream(Encoding.UTF8.GetBytes(WireBodyData.Frames(WireBodyData.StreamTestBodyOpen)));
        using var secondBody = new MemoryStream(Encoding.UTF8.GetBytes(
            WireBodyData.NamedFrame("signal", WireBodyData.StreamTestBodyOpen)));

        var firstFrames = await CollectAsync(framer, firstBody);
        var secondFrames = await CollectAsync(framer, secondBody);

        // One reader per body: the second enumeration starts from a clean frame state and
        // sees its own event name rather than anything carried over from the first body.
        await Assert.That(firstFrames.Single().Name).IsEqualTo(ServerSentEvent.DefaultName);
        await Assert.That(firstFrames.Single().Data).IsEqualTo(WireBodyData.StreamTestBodyOpen);
        await Assert.That(secondFrames.Single().Name).IsEqualTo("signal");
        await Assert.That(secondFrames.Single().Data).IsEqualTo(WireBodyData.StreamTestBodyOpen);
    }

    private static async Task<List<ServerSentEvent>> CollectAsync(ServerSentEventFramer framer, Stream body)
    {
        var frames = new List<ServerSentEvent>();
        await foreach (var frame in framer.ReadAsync(body, CancellationToken.None))
        {
            frames.Add(frame);
        }

        return frames;
    }
}
