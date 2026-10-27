using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

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
        await Assert.That(TestFrames.ReadText(firstFrames.Single().Data)).IsEqualTo(WireBodyData.StreamTestBodyOpen);
        await Assert.That(secondFrames.Single().Name).IsEqualTo("signal");
        await Assert.That(TestFrames.ReadText(secondFrames.Single().Data)).IsEqualTo(WireBodyData.StreamTestBodyOpen);
    }

    [Test]
    public async Task ReadAsync_Should_Keep_One_Bodys_Data_While_Another_Body_Advances()
    {
        var framer = new ServerSentEventFramer();
        using var firstBody = ChunkedStream.Of(WireBodyData.Frames("alpha1", "alpha2"));
        using var secondBody = ChunkedStream.Of(WireBodyData.Frames("bravo1", "bravo2"));
        await using var first = framer.ReadAsync(firstBody, CancellationToken.None).GetAsyncEnumerator();
        await using var second = framer.ReadAsync(secondBody, CancellationToken.None).GetAsyncEnumerator();
        await Assert.That(await first.MoveNextAsync()).IsTrue();
        var held = first.Current.Data;
        await Assert.That(TestFrames.ReadText(held)).IsEqualTo("alpha1");

        await Assert.That(await second.MoveNextAsync()).IsTrue();

        // A different body must not overwrite memory whose owning enumeration has not advanced.
        await Assert.That(TestFrames.ReadText(held)).IsEqualTo("alpha1");
        var firstPayloads = await ReadPayloadsAsync(first);
        var secondPayloads = await ReadPayloadsAsync(second);
        await Assert.That(firstPayloads).IsEquivalentTo(["alpha1", "alpha2"], StringComparer.Ordinal, CollectionOrdering.Matching);
        await Assert.That(secondPayloads).IsEquivalentTo(["bravo1", "bravo2"], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    private static async Task<List<string>> ReadPayloadsAsync(IAsyncEnumerator<ServerSentEvent> frames)
    {
        var payloads = new List<string> { TestFrames.ReadText(frames.Current.Data) };
        while (await frames.MoveNextAsync())
        {
            payloads.Add(TestFrames.ReadText(frames.Current.Data));
        }

        return payloads;
    }

    private static async Task<List<ServerSentEvent>> CollectAsync(ServerSentEventFramer framer, Stream body)
    {
        var frames = new List<ServerSentEvent>();
        await foreach (var frame in framer.ReadAsync(body, CancellationToken.None))
        {
            frames.Add(TestFrames.Copy(frame));
        }

        return frames;
    }
}
