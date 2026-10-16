using System.Runtime.CompilerServices;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Substitutes the framer seam: yields a scripted frame sequence and records what it framed.</summary>
internal sealed class ScriptedFramer : IEventStreamFramer
{
    private readonly IReadOnlyList<ServerSentEvent> _frames;

    public ScriptedFramer(params ServerSentEvent[] frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        _frames = frames;
    }

    public Stream? FramedStream { get; private set; }

    public IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        FramedStream = stream;
        return EnumerateAsync(cancellationToken);
    }

    private async IAsyncEnumerable<ServerSentEvent> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var frame in _frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
        }
    }
}
