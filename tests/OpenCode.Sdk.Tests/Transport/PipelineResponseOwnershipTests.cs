using System.Net;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineResponseOwnershipTests
{
    private const string OperationId = "test.operation";

    [Test]
    public async Task ExecuteAsync_Should_Dispose_Response_Content_After_Success()
    {
        using var content = new DisposalTrackingContent(string.Empty);
        using var response = new DisposalTrackingResponse(HttpStatusCode.OK, content);
        using var handler = CreateHandler(response);
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None);

        await Assert.That(response.IsDisposed).IsTrue();
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_Should_Drain_And_Ignore_An_Unexpected_No_Content_Body()
    {
        using var content = new ReadObservingContent("{\"unexpected\":true}"u8.ToArray());
        using var response = new DisposalTrackingResponse(HttpStatusCode.NoContent, content);
        using var handler = CreateHandler(response);
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var result = await pipeline.ExecuteAsync(OperationId,
            HttpMethod.Delete,
            "/api/shell/sh_1",
            ShellRemoveResponseAdapter.Instance,
            options: null,
            CancellationToken.None);

        // The body a no-content success should not carry is drained into the buffer and
        // ignored by the materializer (canon); nothing of it reaches the envelope.
        await Assert.That(result.Status).IsEqualTo(204);
        await Assert.That(content.WasRead).IsTrue();
        await Assert.That(response.IsDisposed).IsTrue();
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_Should_Dispose_Response_Content_After_An_Api_Error()
    {
        using var content = new DisposalTrackingContent(string.Empty);
        using var response = new DisposalTrackingResponse(HttpStatusCode.Unauthorized, content);
        using var handler = CreateHandler(response);
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        var adapter = new RecordingResponseAdapter(static (status, rawBody) => new TestResponse
        {
            Status = status,
            IsError = true,
            RawBody = rawBody,
        });

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None))
            .Throws<OpenCodeApiException>();

        await Assert.That(response.IsDisposed).IsTrue();
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_Should_Dispose_Response_Content_After_A_Redirect_Protocol_Failure_With_NoThrow()
    {
        using var content = new DisposalTrackingContent(string.Empty);
        using var response = new DisposalTrackingResponse(HttpStatusCode.Found, content);
        using var handler = CreateHandler(response);
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get,
                "/api/info",
                new RecordingResponseAdapter(),
                OpenCodeRequestOptions.NoThrow,
                CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(response.IsDisposed).IsTrue();
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_Should_Dispose_Response_Content_After_Caller_Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var content = new CancelingContent(cancellation);
        using var response = new DisposalTrackingResponse(HttpStatusCode.OK, content);
        using var handler = CreateHandler(response);
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(response.IsDisposed).IsTrue();
        await Assert.That(content.IsDisposed).IsTrue();
    }

    private static RecordingHttpHandler CreateHandler(HttpResponseMessage response) => new(_ => response);
}
