using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>Owns setup and cleanup of a canned event connection and decodes setup frames.</summary>
internal sealed class EventBenchmarkSetup : IDisposable
{
    internal CannedResponseHandler Handler { get; }
    internal HttpClient HttpClient { get; }
    internal OpenCodeClient Client { get; }

    internal EventBenchmarkSetup(byte[] bytes, OpenCodeClientOptions options, int? suspendingChunkBytes)
    {
        Handler = new CannedResponseHandler(bytes, "text/event-stream", suspendingChunkBytes: suspendingChunkBytes);
        HttpClient = new HttpClient(Handler);
        Client = new OpenCodeClient(HttpClient, options);
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

    public void Dispose() => Dispose(disposing: true);

    internal void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        Client.Dispose();
        HttpClient.Dispose();
        Handler.Dispose();
    }
}
