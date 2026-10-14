namespace OpenCode.Sdk.Internal;

/// <summary>
/// The production framer: stateless itself, constructing one stateful
/// <see cref="ServerSentEventReader"/> per body so no framing state crosses bodies.
/// </summary>
internal sealed class ServerSentEventFramer : IEventStreamFramer
{
    public IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        new ServerSentEventReader().ReadAsync(stream, cancellationToken);
}
