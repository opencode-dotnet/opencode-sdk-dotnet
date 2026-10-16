using System.Net;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class ResponseBufferingPolicyTests
{
    private const string OperationId = "test.operation";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ProcessAsync_Should_Preserve_The_Caller_Token_During_A_Buffered_Read(bool disposalInduced) =>
        AssertBufferedInterruptionAsync(disposalInduced, disposeClient: false, cancelCaller: true);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public Task ExecuteAsync_Should_Report_Client_Disposal_During_A_Buffered_Read(bool disposalInduced, bool cancelCaller) =>
        AssertBufferedInterruptionAsync(disposalInduced, disposeClient: true, cancelCaller);

    private static async Task AssertBufferedInterruptionAsync(bool disposalInduced, bool disposeClient, bool cancelCaller)
    {
        var pool = new TrackingByteArrayPool();
        using var caller = new CancellationTokenSource();
        using var body = new GatedStream([0x41], chunkCount: 1);
        using var interrupted = new InterruptedBufferingContent();
        using HttpContent content = disposalInduced ? interrupted : new StreamContent(body);
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, bufferPool: pool);

        if (!disposeClient)
        {
            var execution = pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info",
                new RecordingResponseAdapter(), options: null, caller.Token);
            OperationCanceledException? canceled = null;
            try
            {
                await WaitForBufferedReadAsync(disposalInduced, interrupted, body);
            }
            finally
            {
                await caller.CancelOnWorkerAsync();
                try
                {
                    _ = await execution.WaitAsync(HangGuard.Bound);
                }
                catch (OperationCanceledException exception)
                {
                    canceled = exception;
                }
            }

            await Assert.That(canceled).IsNotNull();
            await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(canceled.InnerException).IsNotNull();
        }
        else
        {
            var execution = PipelineStreamTests.CaptureFailureAsync(() => pipeline.ExecuteAsync(
                OperationId, HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, caller.Token));
            Exception? failure;
            try
            {
                await WaitForBufferedReadAsync(disposalInduced, interrupted, body);
                if (cancelCaller)
                {
                    await caller.CancelOnWorkerAsync();
                }

                pipeline.Dispose();
                failure = await execution.WaitAsync(HangGuard.Bound);
            }
            finally
            {
                await caller.CancelOnWorkerAsync();
                _ = await execution.WaitAsync(HangGuard.Bound);
            }

            await PipelineStreamTests.AssertInterruptionAsync(failure, cancelCaller, caller.Token);
        }

        if (disposalInduced)
        {
            await interrupted.ReadCompleted.WaitAsync(HangGuard.Bound);
            await Assert.That(interrupted.IsDisposed).IsTrue();
        }

        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    private static Task WaitForBufferedReadAsync(bool disposalInduced, InterruptedBufferingContent interrupted, GatedStream body) =>
        disposalInduced ? interrupted.ReadStarted.WaitAsync(HangGuard.Bound) : body.WaitForReadAsync(HangGuard.Bound);

    [Test]
    public async Task ExecuteAsync_Should_Return_The_Pooled_Body_After_Success()
    {
        var pool = new TrackingByteArrayPool();
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(WireBodyData.InfoOk),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, bufferPool: pool);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        // The tracking pool pre-fills spare capacity with 0xFF, so the adapter seeing the
        // exact body also proves decoding stayed inside the filled length.
        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(WireBodyData.InfoOk);
        await Assert.That(pool.RentCount).IsGreaterThan(0);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_Should_Grow_And_Return_Every_Array_For_An_Undeclared_Length()
    {
        var payload = "{\"value\":\"" + new string('x', 2048) + "\"}";
        var pool = new TrackingByteArrayPool();
        using var content = new StreamContent(ChunkedStream.OfBytes(payload, chunkSize: payload.Length / 4));
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, bufferPool: pool);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        // No declared length starts the buffer small, so a 2 KB body forces the
        // grow-and-copy path; every rented array must still come back exactly once.
        await Assert.That(adapter.AdaptedRawBody).IsEqualTo(payload);
        await Assert.That(pool.RentCount).IsGreaterThan(1);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_Should_Return_The_Array_When_The_Body_Faults_Mid_Copy()
    {
        var pool = new TrackingByteArrayPool();
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FaultingStream(Encoding.UTF8.GetBytes("{\"healthy\":"))),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, bufferPool: pool);

        _ = await Assert
            .That(async () => _ = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, CancellationToken.None))
            .Throws<OpenCodeTransportException>();

        await Assert.That(pool.RentCount).IsGreaterThan(0);
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task ExecuteAsync_Should_Own_No_Arrays_After_Caller_Cancellation()
    {
        var pool = new TrackingByteArrayPool();
        using var callerCancellation = new CancellationTokenSource();
        using var content = new BlockingContent();
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, bufferPool: pool);

        var execution = pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get, "/api/info", new RecordingResponseAdapter(), options: null, callerCancellation.Token);
        await content.ReadStarted.WaitAsync(TimeSpan.FromSeconds(1));
        await callerCancellation.CancelOnWorkerAsync();

        OperationCanceledException? cancellation = null;
        try
        {
            _ = await execution;
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }

        await Assert.That(cancellation).IsNotNull();
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ExecuteAsync_Should_Buffer_An_Empty_Body_Without_Leaking()
    {
        var pool = new TrackingByteArrayPool();
        using var handler = new RecordingHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, bufferPool: pool);
        var adapter = new RecordingResponseAdapter();

        _ = await pipeline.ExecuteAsync(OperationId, HttpMethod.Get, "/api/info", adapter, options: null, CancellationToken.None);

        await Assert.That(adapter.AdaptedRawBody).IsEmpty();
        await Assert.That(pool.OutstandingCount).IsEqualTo(0);
    }
}
