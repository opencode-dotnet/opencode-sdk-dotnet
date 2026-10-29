using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

using TUnit.Assertions.Enums;

using static OpenCode.Sdk.Tests.Streaming.Support.ServerSentEventReaderData;

namespace OpenCode.Sdk.Tests.Streaming.Support;

internal sealed class ServerSentEventReaderScenario(
    int maxFrameCharacters = ServerSentEventReader.DefaultMaxFrameCharacters)
{
    public async Task AssertSingleFrameAsync(Stream stream, string expectedName, string expectedData)
    {
        var frames = await ReadEventsAsync(stream);

        await Assert.That(frames.Single().Name).IsEqualTo(expectedName);
        await Assert.That(TestFrames.ReadText(frames.Single().Data)).IsEqualTo(expectedData);
    }

    public async Task AssertBoundaryAsync(byte[][] chunks, string[] expected)
    {
        using var accepted = ChunkedStream.OfBytes(chunks);
        var frames = await ReadAllAsync(accepted);
        await Assert.That(frames).IsEquivalentTo(expected, StringComparer.Ordinal, CollectionOrdering.Matching);

        using var refused = ChunkedStream.OfBytes(chunks);
        _ = await new ServerSentEventReaderScenario(maxFrameCharacters - 1).AssertRefusalAsync(refused,
            SizeLimitMessage);
    }

    public async Task<OpenCodeTransportException> AssertRefusalAsync(Stream stream, string message)
    {
        var exception = await Assert.That(async () => _ = await ReadAllAsync(stream))
            .Throws<OpenCodeTransportException>().WithMessage(message, StringComparison.Ordinal);
        // The assertion guarantees a refusal rather than successful completion.
        return exception!;
    }

    public async Task<IReadOnlyList<byte[]>> ReadUntilRefusalAsync(Stream stream, string message)
    {
        var delivered = new List<byte[]>();
        _ = await Assert.That(async () =>
        {
            await foreach (var frame in new ServerSentEventReader(maxFrameCharacters).ReadAsync(stream, CancellationToken.None))
            {
                delivered.Add(frame.Data.ToArray());
            }
        }).Throws<OpenCodeTransportException>()
            .WithMessage(message, StringComparison.Ordinal);

        return delivered;
    }

    public async Task<List<string>> ReadAllAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        var frames = new List<string>();
        await foreach (var frame in new ServerSentEventReader(maxFrameCharacters).ReadAsync(stream, cancellationToken))
        {
            frames.Add(TestFrames.ReadText(frame.Data));
        }

        return frames;
    }

    public async Task<List<ServerSentEvent>> ReadEventsAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        var frames = new List<ServerSentEvent>();
        await foreach (var frame in new ServerSentEventReader(maxFrameCharacters).ReadAsync(stream, cancellationToken))
        {
            frames.Add(TestFrames.Copy(frame));
        }

        return frames;
    }
}
