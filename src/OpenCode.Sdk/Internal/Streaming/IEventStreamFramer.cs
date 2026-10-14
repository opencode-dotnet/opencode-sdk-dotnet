namespace OpenCode.Sdk.Internal;

/// <summary>
/// Frames one response body stream into server-sent events. The seam the stream plane
/// sequences through; plane tests substitute a scripted framer here.
/// </summary>
internal interface IEventStreamFramer
{
    /// <summary>Reads the body as an event stream; one enumeration frames one body.</summary>
    public IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream, CancellationToken cancellationToken);
}
