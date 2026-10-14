using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class PipelineStreamTests
{
    private const string EventStreamMediaType = "text/event-stream";
    private const string FirstPayload = "{\"value\":\"first\"}";
    private const string SecondPayload = "{\"value\":\"second\"}";
    private const string FirstFrame = "data: " + FirstPayload + "\n\n";
    private const string FirstAndSecondFrames = FirstFrame + "data: " + SecondPayload + "\n\n";

    [Test]
    public async Task ExecuteStreamAsync_Should_Yield_Every_Frame_Payload()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(FirstAndSecondFrames));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var payloads = await CollectAsync(pipeline);

        await Assert
            .That(payloads
                .Select(static payload => payload.Value)
                .SequenceEqual(["first", "second"], StringComparer.Ordinal))
            .IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task ExecuteStreamAsync_Should_Yield_The_First_Frame_Before_The_Body_Ends()
    {
        using var body = new BlockingStream(Encoding.UTF8.GetBytes(FirstFrame));
        using var handler = new RecordingHttpHandler(_ => EventStreamOf(body));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        var stream = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), CancellationToken.None);

        await using var enumerator = stream.GetAsyncEnumerator(CancellationToken.None);
        var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(moved).IsTrue();
        await Assert.That(enumerator.Current.Value).IsEqualTo("first");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Sequence_Frames_Through_The_Framer_Seam()
    {
        var framer = new ScriptedFramer(
            new ServerSentEvent(ServerSentEvent.DefaultName, FirstPayload),
            new ServerSentEvent(ServerSentEvent.DefaultName, SecondPayload));
        using var handler = new RecordingHttpHandler(static _ => EventStream(string.Empty));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, framer: framer);

        var payloads = await CollectAsync(pipeline);

        // The plane consumes whatever the framer seam yields: the scripted frames arrive in
        // order and dispatch exactly as wire-framed ones would.
        await Assert
            .That(payloads
                .Select(static payload => payload.Value)
                .SequenceEqual(["first", "second"], StringComparer.Ordinal))
            .IsTrue();
        await Assert.That(framer.FramedStream).IsNotNull();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Decorate_The_Request_Like_Any_Other()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(FirstFrame));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, password: "secret",
            location: new LocationSelector
            {
                Directory = "/repo"
            });

        _ = await CollectAsync(pipeline);

        var request = handler.Requests.Single();
        var expected = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:secret"))}";
        await Assert.That(request.Authorization).IsEqualTo(expected);
        await Assert.That(request.UserAgent).IsEqualTo(UserAgentPolicy.Resolve().ToString());
        await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Frepo");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Throw_The_Typed_Error_Before_Opening_The_Stream()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(WireBodyData.SessionNotFoundError),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline, new TestStreamAdapter("SessionNotFoundError")))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<SessionNotFoundError>();
        await Assert.That(exception.RawBody).Contains("SessionNotFoundError");
    }

    /// <summary>
    /// A stream has no envelope, so the 401 diagnostic is only ever readable as a message — and the
    /// streaming spine reaches the same failure builder the one-shot spine does.
    /// </summary>
    [Test]
    [Arguments(null, true)]
    [Arguments("secret", false)]
    public async Task ExecuteStreamAsync_Should_Name_A_Missing_Credential_Only_When_None_Was_Configured(
        string? password, bool expectHint)
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(string.Empty),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, password: password);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(401);
        await Assert.That(exception.Message.Contains(
            "The client sent no credential", StringComparison.Ordinal)).IsEqualTo(expectHint);
    }

    [Test]
    [NotInParallel]
    public async Task ExecuteStreamAsync_Should_Fail_A_Stalled_Error_Body_At_The_Progress_Window()
    {
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var content = new BlockingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, networkTimeout: TimeSpan.FromMilliseconds(200));
        var stream = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), callerCancellation.Token);
        await using var enumerator = stream.GetAsyncEnumerator(callerCancellation.Token);

        var exception = await Assert
            .That(async () => _ = await enumerator.MoveNextAsync())
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsNotNull();
        await Assert.That(callerCancellation.IsCancellationRequested).IsFalse();
        await content.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await content.ReadCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(content.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Refuse_A_Success_That_Is_Not_An_Event_Stream()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":\"first\"}"),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Treat_An_Undeclared_2xx_As_A_Protocol_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var content = new StringContent(FirstFrame);
            content.Headers.ContentType = new MediaTypeHeaderValue(EventStreamMediaType);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = content,
            };
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Treat_A_Redirect_As_A_Protocol_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent(string.Empty),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.Message).Contains("302");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Treat_A_Malformed_Frame_As_A_Protocol_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream("data: not json\n\n"));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        _ = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Honor_Cancellation()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(FirstFrame));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert
            .That(async () => _ = await CollectAsync(pipeline, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Read_A_Mid_Stream_Connection_Failure_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ =>
            EventStreamOf(new FaultingStream(Encoding.UTF8.GetBytes(FirstFrame))));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<IOException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Read_Transport_Cancellation_As_A_Transport_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStreamOf(new CancelingStream()));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.InnerException).IsTypeOf<TaskCanceledException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Expose_A_Typed_Stream_Failure_Cause()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(
            FirstFrame + $"event: {TestStreamAdapter.StreamFailureEventName}\ndata: [{{\"tag\":\"Die\",\"detail\":\"boom\"}}]\n\n"));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeStreamFailureException>();

        var cause = (TestStreamFailureCause)exception!.Causes.Single();
        await Assert.That(cause.Tag).IsEqualTo("Die");
        await Assert.That(cause.Detail).IsEqualTo("boom");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Treat_A_Malformed_Failure_Cause_As_A_Protocol_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(
            $"event: {TestStreamAdapter.StreamFailureEventName}\ndata: not json\n\n"));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception).IsNotTypeOf<OpenCodeStreamFailureException>();
        await Assert.That(exception!.InnerException).IsTypeOf<System.Text.Json.JsonException>();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Treat_A_Null_Failure_Cause_As_A_Protocol_Failure()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(
            $"event: {TestStreamAdapter.StreamFailureEventName}\ndata: null\n\n"));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception).IsNotTypeOf<OpenCodeStreamFailureException>();
        await Assert.That(exception!.Message).Contains("null failure cause");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Refuse_A_Frame_Named_Outside_The_Contract()
    {
        using var handler = new RecordingHttpHandler(static _ =>
            EventStream("event: surprise\n" + FirstFrame));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.Message).Contains("surprise");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Accept_An_Event_Stream_Content_Type_In_Any_Case()
    {
        using var handler = new RecordingHttpHandler(static _ =>
        {
            var content = new StringContent(FirstFrame);
            content.Headers.ContentType = new MediaTypeHeaderValue("Text/Event-Stream");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var payloads = await CollectAsync(pipeline);

        await Assert.That(payloads.Single().Value).IsEqualTo("first");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Refuse_A_Null_Frame_Payload()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream("data: null\n\n"));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = await Assert
            .That(async () => _ = await CollectAsync(pipeline))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.Message).Contains("null");
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Dispose_The_Body_When_The_Consumer_Stops_Early()
    {
        using var body = ChunkedStream.Of(FirstAndSecondFrames);
        using var handler = new RecordingHttpHandler(_ => EventStreamOf(body));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var frames = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), CancellationToken.None);
        await using (var enumerator = frames.GetAsyncEnumerator(CancellationToken.None))
        {
            _ = await enumerator.MoveNextAsync();
        }

        await Assert.That(body.Disposed).IsTrue();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Refuse_After_Dispose()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        var pipeline = PipelineFactory.Create(httpClient);
        pipeline.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => _ = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), CancellationToken.None));

        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Refuse_Enumeration_That_Starts_After_Dispose()
    {
        using var handler = new RecordingHttpHandler(static _ => EventStream(FirstFrame));
        using var httpClient = new HttpClient(handler);
        var pipeline = PipelineFactory.Create(httpClient);
        var stream = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), CancellationToken.None);
        pipeline.Dispose();

        _ = await Assert
            .That(async () =>
            {
                await using var enumerator = stream.GetAsyncEnumerator(CancellationToken.None);
                _ = await enumerator.MoveNextAsync();
            })
            .Throws<ObjectDisposedException>();

        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Refuse_A_Route_Without_A_Leading_Slash()
    {
        using var handler = new RecordingHttpHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient);

        var exception = Assert.Throws<ArgumentException>(() => _ = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "api/event", new TestStreamAdapter(), CancellationToken.None));

        await Assert.That(exception.ParamName).IsEqualTo("route");
    }

    private static HttpResponseMessage EventStreamOf(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(EventStreamMediaType);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static HttpResponseMessage EventStream(string body)
    {
        var content = new StringContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private static async Task<List<TestBody>> CollectAsync(Pipeline pipeline,
        TestStreamAdapter? adapter = null,
        CancellationToken cancellationToken = default)
    {
        var payloads = new List<TestBody>();
        var stream = pipeline.ExecuteStreamAsync(
            HttpMethod.Get, "/api/event", adapter ?? new TestStreamAdapter(), cancellationToken);
        await foreach (var payload in stream)
        {
            payloads.Add(payload);
        }

        return payloads;
    }
}
