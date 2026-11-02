#if NET
using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PooledJsonContentSyncReadTests
{
    private const int BodyLength = 3;

    [Test]
    public async Task SendAsync_Should_Allow_A_Handlers_Synchronous_Reads()
    {
        using var handler = new SynchronousReadHandler();
        using var http = new HttpClient(handler);
        using var client = new OpenCodeClient(http, new OpenCodeClientOptions { Endpoint = new Uri("https://test.invalid") });
        var body = new SessionViewRequest { Idle = 123 };
        var session = client.Sessions.GetSessionClient("sync-read");

        _ = await session.MarkViewedAsync(body);
        _ = await session.MarkViewedAsync(body);

        await Assert.That(handler.Bodies.Count).IsEqualTo(2);
        foreach (var bytes in handler.Bodies)
        {
            await Assert.That(bytes).IsEquivalentTo(JsonSerializer.SerializeToUtf8Bytes(body,
                OpenCodeJsonContext.Default.SessionViewRequest), CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task CopyTo_Should_Abandon_Storage_After_A_Faulted_Write()
    {
        using var content = CreateContent(out var pool);
        using var stream = new WriteStream(fault: true);

        _ = await Assert.That(() => content.CopyTo(stream, null, CancellationToken.None)).Throws<InvalidOperationException>();
        content.Dispose();

        await Assert.That(pool.ReturnCount).IsEqualTo(0);
    }

    [Test]
    public async Task CopyTo_Should_Return_Cleared_Storage_After_A_Completed_Write()
    {
        byte[] returnedPrefix = [];
        byte[] expectedBytes = [0xff, 0xff, 0xff];
        var pool = new TrackingByteArrayPool(onReturn: buffer => returnedPrefix = buffer.AsSpan(0, BodyLength).ToArray());
        using var content = new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
        using var stream = new WriteStream();

        CopyTo(content, stream);
        content.Dispose();

        await AssertBytesAsync(stream.ToArray(), expectedBytes);
        await AssertBytesAsync(returnedPrefix, new byte[BodyLength]);
        await Assert.That(pool.ReturnCount).IsEqualTo(1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    private static async Task AssertBytesAsync(byte[] actual, byte[] expected) =>
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);

    private static PooledJsonContent CreateContent(out TrackingByteArrayPool pool)
    {
        pool = new TrackingByteArrayPool();
        return new PooledJsonContent(pool, pool.Rent(BodyLength), BodyLength);
    }

    private static void CopyTo(HttpContent content, Stream stream) =>
        content.CopyTo(stream, null, CancellationToken.None);

    private sealed class SynchronousReadHandler : DelegatingHandler
    {
        public List<byte[]> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Both SDK calls supply a JSON body; each request exercises a different sync entry point.
            Bodies.Add(Bodies.Count == 0 ? ReadStream(request.Content!, cancellationToken) : CopyContent(request.Content!));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static byte[] ReadStream(HttpContent content, CancellationToken cancellationToken)
        {
            using var destination = new MemoryStream();
            using var source = content.ReadAsStream(cancellationToken);
            source.CopyTo(destination);
            return destination.ToArray();
        }

        private static byte[] CopyContent(HttpContent content)
        {
            using var destination = new MemoryStream();
            CopyTo(content, destination);
            return destination.ToArray();
        }
    }

    private sealed class WriteStream(bool fault = false) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (fault)
            {
                throw new InvalidOperationException("The test stream refused the write.");
            }

            base.Write(buffer, offset, count);
        }
    }
}
#endif
