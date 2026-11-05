using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>Owns setup and cleanup of a canned event connection and decodes setup frames.</summary>
internal sealed class EventBenchmarkConnection : IDisposable
{
    private readonly HttpClient _httpClient;

    public void Dispose() => Dispose(disposing: true);

    internal CannedResponseHandler Handler { get; }
    internal OpenCodeClient Client { get; }

    internal EventBenchmarkConnection(byte[] bytes, OpenCodeClientOptions options, int? suspendingChunkBytes)
    {
        Handler = new CannedResponseHandler(bytes, "text/event-stream", suspendingChunkBytes: suspendingChunkBytes);
        _httpClient = new HttpClient(Handler);
        Client = new OpenCodeClient(_httpClient, options);
    }

    internal static async Task<IReadOnlyList<byte[]>> DecodeFramesAsync(byte[] bytes)
    {
        var frames = new List<byte[]>();
        using var stream = new MemoryStream(bytes);
        await foreach (var frame in new ServerSentEventReader().ReadAsync(stream, CancellationToken.None).ConfigureAwait(false))
        {
            frames.Add(frame.Data.ToArray());
        }
        return frames;
    }

    internal void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        Client.Dispose();
        _httpClient.Dispose();
        Handler.Dispose();
    }
}
