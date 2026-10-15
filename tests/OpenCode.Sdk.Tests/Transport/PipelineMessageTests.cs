using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineMessageTests
{
    private const string OperationId = "test.operation";

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
            OperationId = OperationId,
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
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:4096/api/info"));
        var message = new PipelineMessage
        {
            OperationId = OperationId,
            Request = request,
            Body = new ResponseBody(pool, pool.Rent(16), 3),
        };

        message.Dispose();

        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task Dispose_Should_Tolerate_A_Message_Without_A_Response()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:4096/api/info"));
        var message = new PipelineMessage { OperationId = OperationId, Request = request };

        message.Dispose();

        await Assert.That(message.Response).IsNull();
    }
}
