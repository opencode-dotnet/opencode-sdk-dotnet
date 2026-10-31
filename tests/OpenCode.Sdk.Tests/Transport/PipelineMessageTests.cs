using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.Tests.Transport.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineMessageTests
{
    [Test]
    public async Task Dispose_Should_Release_The_Request_And_The_Response()
    {
        using var requestContent = new DisposalTrackingContent("{}");
        using var responseContent = new DisposalTrackingContent("{}");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://localhost:4096/api/session"))
        {
            Content = requestContent,
        };
        using var response = new DisposalTrackingResponse(HttpStatusCode.OK, responseContent);
        var message = new PipelineMessage
        {
            NetworkTimeout = NetworkTimeouts.DefaultNetworkTimeout,
            Request = request,
            Response = response,
        };

        message.Dispose();

        await Assert.That(requestContent.IsDisposed).IsTrue();
        await Assert.That(response.IsDisposed).IsTrue();
        await Assert.That(responseContent.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Dispose_Should_Return_The_Pooled_Body()
    {
        var pool = new TrackingByteArrayPool();
        var bytes = pool.Rent(16);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:4096/api/info"));
        var message = new PipelineMessage
        {
            NetworkTimeout = NetworkTimeouts.DefaultNetworkTimeout,
            Request = request,
        };
        message.AdoptBody(pool, bytes, 3);

        await Assert.That(message.BodyLength).IsEqualTo(3);
        await Assert.That(message.BodyBytes).IsSameReferenceAs(bytes);

        message.Dispose();

        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
        await Assert.That(message.BodyLength).IsEqualTo(0);
        await Assert.That(message.BodyBytes).IsEmpty();

        message.Dispose();

        await Assert.That(pool.ReturnCount).IsEqualTo(1);
    }

    [Test]
    public async Task Dispose_Should_Tolerate_A_Message_Without_A_Response()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:4096/api/info"));
        var message = new PipelineMessage
        {
            Request = request,
            NetworkTimeout = NetworkTimeouts.DefaultNetworkTimeout,
        };

        await Assert.That(message.BodyLength).IsEqualTo(0);
        await Assert.That(message.BodyBytes).IsEmpty();

        message.Dispose();

        await Assert.That(message.Response).IsNull();
    }
}
