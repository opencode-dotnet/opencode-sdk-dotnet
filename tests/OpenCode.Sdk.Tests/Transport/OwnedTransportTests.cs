using System.Net;
using System.Text;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

[NotInParallel]
public sealed class OwnedTransportTests
{
    private const string OperationId = "test.operation";

#if NET
    [Test]
    public async Task CreateOwnedHttpHandler_Should_Expose_The_Modern_Owned_Policy()
    {
        using var handler = TransportPolicy.CreateOwnedHttpHandler(new Uri("http://localhost"));

        await Assert.That(handler).IsTypeOf<SocketsHttpHandler>();
        var socketsHandler = (SocketsHttpHandler)handler;
        await Assert.That(socketsHandler.AllowAutoRedirect).IsFalse();
        await Assert.That(socketsHandler.PooledConnectionLifetime).IsEqualTo(TimeSpan.FromSeconds(120));
    }
#endif

    /// <summary>
    /// The pinned document declares a required body on a DELETE, and .NET Framework's handler is
    /// the one that refuses a body on the wrong verb, so the body is asserted off the socket on
    /// every target rather than off a stub the platform never sees.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_Should_Send_A_Delete_Body_Through_The_Real_Handler()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.NoContent,
        });
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = server.Endpoint });

        var response = await client.Worktrees.RemoveWorktreeAsync(new WorktreeRemoveRequest
        {
            ProjectId = "prj_1",
            Directory = "/repo/feature",
            Force = true,
        });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = server.Requests.Single();
        await Assert.That(request.Method).IsEqualTo("DELETE");
        await Assert.That(request.Path).IsEqualTo("/api/worktree");
        await Assert.That(request.Body).IsEqualTo("{\"projectID\":\"prj_1\",\"directory\":\"/repo/feature\",\"force\":true}");
    }

    [Test]
    public async Task ExecuteAsync_Should_Surface_A_Redirect_Through_The_Real_Handler()
    {
        await using var server = LoopbackHttpServer.Start(path => path switch
        {
            "/api/info" => new LoopbackHttpResponse
            {
                StatusCode = HttpStatusCode.Found,
                Location = "/redirect-target",
            },
            "/redirect-target" => new LoopbackHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
                Body = WireBodyData.InfoOk,
            },
            _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.InternalServerError },
        });
        using var client = new OpenCodeClient(new OpenCodeClientOptions { Endpoint = server.Endpoint });

        var exception = await Assert
            .That(async () => _ = await client.Server.GetInfoAsync())
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.Message).Contains("302");
        await Assert.That(server.RequestPaths).IsEquivalentTo(["/api/info"]);
    }

    [Test]
    public async Task ExecuteAsync_Should_Fail_A_Stalled_Real_Handler_Body_At_The_Progress_Window()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
            ContentType = "application/json",
            Body = WireBodyData.InfoOk,
            KeepOpen = true,
        });
        var time = new ManualTimeProvider();
        using var ownedHandler = TransportPolicy.CreateOwnedHttpHandler(server.Endpoint);
        using var handler = new StalledBodySignal(ownedHandler, Encoding.UTF8.GetByteCount(WireBodyData.InfoOk));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, endpoint: server.Endpoint, time: time);

        // The client's own timeout never guards a post-headers read; the progress window is
        // the only timer over a real stalled socket, interrupting through the read token on
        // modern targets and through content disposal on net472. The message names the phase:
        // a window that fired before the headers would fail the send instead and still throw
        // the same exception type. The window runs on the test's clock, which passes it only
        // once the read that stalls has started: the body has arrived and the window was
        // restarted for every chunk of it before that read, so no restart can race the move.
        var execution = pipeline.ExecuteAsync(OperationId,
            HttpMethod.Get,
            "/api/info",
            new RecordingResponseAdapter(),
            options: null,
            CancellationToken.None);
        await handler.BodyStalled.WaitAsync(HangGuard.Bound);
        time.Advance(PipelineMessage.DefaultNetworkTimeout);

        var exception = await Assert
            .That(execution.WaitAsync(HangGuard.Bound))
            .Throws<OpenCodeTransportException>();

        await Assert.That(exception!.Message).IsEqualTo("The opencode response body could not be read.");
#if NET472
        Task[] disconnectTasks = [server.ClientDisconnected];
        await Task.WhenAll(disconnectTasks).WaitAsync(HangGuard.Bound);
#endif
    }

#if NET472
    [Test]
    public async Task Create_Should_Configure_The_Endpoint_ServicePoint_For_A_Long_Lived_Client()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
        });

        var defaultConnectionLimit = ServicePointManager.DefaultConnectionLimit;
        using var pipeline = Pipeline.Create(new OpenCodeClientOptions { Endpoint = server.Endpoint });
        var servicePoint = ServicePointManager.FindServicePoint(server.Endpoint, WebRequest.DefaultWebProxy);

        await Assert.That(servicePoint.ConnectionLimit).IsEqualTo(int.MaxValue);
        await Assert.That(servicePoint.ConnectionLeaseTimeout).IsEqualTo(120_000);
        await Assert.That(ServicePointManager.DefaultConnectionLimit).IsEqualTo(defaultConnectionLimit);
    }

    [Test]
    public async Task Create_Should_Configure_The_Proxy_ServicePoint_Used_By_The_Owned_Handler()
    {
        await using var proxyServer = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
        });
        var endpoint = new Uri("http://opencode.invalid");
        var previousProxy = WebRequest.DefaultWebProxy;
        var proxy = new WebProxy(proxyServer.Endpoint, false);
        try
        {
            WebRequest.DefaultWebProxy = proxy;
            using var pipeline = Pipeline.Create(new OpenCodeClientOptions { Endpoint = endpoint });

            var response = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get,
                "/api/info",
                new RecordingResponseAdapter(),
                options: null,
                CancellationToken.None);
            var servicePoint = ServicePointManager.FindServicePoint(endpoint, proxy);

            await Assert.That(response.Status).IsEqualTo(200);
            await Assert.That(servicePoint.ConnectionLimit).IsEqualTo(int.MaxValue);
            await Assert.That(servicePoint.ConnectionLeaseTimeout).IsEqualTo(120_000);
        }
        finally
        {
            WebRequest.DefaultWebProxy = previousProxy;
        }
    }

    [Test]
    public async Task ExecuteAsync_Should_Not_Starve_Behind_Two_Same_Authority_Streams_On_Net472()
    {
        await using var server = LoopbackHttpServer.Start(path => path switch
        {
            "/api/event" => new LoopbackHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "text/event-stream",
                Body = WireBodyData.Frames(WireBodyData.StreamTestBodyOpen),
                KeepOpen = true,
            },
            "/api/info" => new LoopbackHttpResponse
            {
                StatusCode = HttpStatusCode.OK,
                ContentType = "application/json",
            },
            _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.InternalServerError },
        });
        using var pipeline = Pipeline.Create(new OpenCodeClientOptions { Endpoint = server.Endpoint });
        await using var first = pipeline.ExecuteStreamAsync(OperationId,
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), null, CancellationToken.None).GetAsyncEnumerator();
        await using var second = pipeline.ExecuteStreamAsync(OperationId,
            HttpMethod.Get, "/api/event", new TestStreamAdapter(), null, CancellationToken.None).GetAsyncEnumerator();
        var firstOpened = await first.MoveNextAsync();
        var secondOpened = await second.MoveNextAsync();

        var response = await pipeline.ExecuteAsync(OperationId,
                HttpMethod.Get,
                "/api/info",
                new RecordingResponseAdapter(),
                options: null,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(firstOpened).IsTrue();
        await Assert.That(secondOpened).IsTrue();
        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(server.RequestPaths.Count(static path => path == "/api/event")).IsEqualTo(2);
        await Assert.That(server.RequestPaths.Count(static path => path == "/api/info")).IsEqualTo(1);
    }

    [Test]
    public async Task ExecuteStreamAsync_Should_Cancel_An_Idle_Real_Handler_Read_On_Net472()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.OK,
            ContentType = "text/event-stream",
            Body = WireBodyData.Frames(WireBodyData.StreamTestBodyOpen),
            KeepOpen = true,
        });
        using var pipeline = Pipeline.Create(new OpenCodeClientOptions { Endpoint = server.Endpoint });
        using var cancellation = new CancellationTokenSource();
        var enumerator = pipeline.ExecuteStreamAsync(OperationId,
                HttpMethod.Get, "/api/event", new TestStreamAdapter(), null, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Task<bool>? pendingRead = null;
        try
        {
            await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
            pendingRead = enumerator.MoveNextAsync().AsTask();
            await cancellation.CancelOnWorkerAsync();
            Task[] pendingReads = [pendingRead];

            _ = await Assert
                .That(async () => await Task.WhenAll(pendingReads).WaitAsync(HangGuard.Bound))
                .Throws<OperationCanceledException>();

            await Assert.That(cancellation.IsCancellationRequested).IsTrue();
            Task[] disconnectTasks = [server.ClientDisconnected];
            await Task.WhenAll(disconnectTasks).WaitAsync(HangGuard.Bound);
        }
        finally
        {
            server.ReleaseResponses();
            await SettleAfterReleaseAsync(pendingRead);
            await enumerator.DisposeAsync();
        }
    }

    private static async Task SettleAfterReleaseAsync(Task? pendingRead)
    {
        if (pendingRead is null)
        {
            return;
        }

        try
        {
            Task[] pendingReads = [pendingRead];
            await Task.WhenAll(pendingReads).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or OpenCodeTransportException or TimeoutException)
        {
            _ = exception;
        }
    }
#endif

    /// <summary>Reports when the inner handler has returned a response, which is when its headers are in.</summary>
    /// <summary>
    /// Signals the read that stalls: the first read the pipeline starts once the response's
    /// expected body bytes have all been handed over. The pipeline restarts its progress window
    /// before every read, so by the time that read starts no restart is left to race a clock move.
    /// </summary>
    private sealed class StalledBodySignal(HttpMessageHandler inner, int bodyLength) : DelegatingHandler(inner)
    {
        private readonly TaskCompletionSource<bool> _bodyStalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once a read starts after the whole body was read.</summary>
#pragma warning disable VSTHRD003 // Test signals complete on their own source and resume asynchronously; exposing the task is the signal's contract.
        public Task BodyStalled => _bodyStalled.Task;
#pragma warning restore VSTHRD003

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Content = new SignallingContent(response.Content, bodyLength, _bodyStalled);
            return response;
        }
    }

    /// <summary>
    /// The real response content, read through a stream that counts what it hands over.
    /// Disposing it disposes the real content, so the pipeline's interruption still reaches the
    /// socket on every target.
    /// </summary>
    private sealed class SignallingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly int _bodyLength;
        private readonly TaskCompletionSource<bool> _stalled;

        public SignallingContent(HttpContent inner, int bodyLength, TaskCompletionSource<bool> stalled)
        {
            _inner = inner;
            _bodyLength = bodyLength;
            _stalled = stalled;
            foreach (var header in inner.Headers)
            {
                _ = Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new SignallingStream(await _inner.ReadAsStreamAsync(), _bodyLength, _stalled);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await (await CreateContentReadStreamAsync()).CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SignallingStream(Stream inner, int bodyLength, TaskCompletionSource<bool> stalled) : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

#if NET
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
#else
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            SignalWhenDrained();
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            _read += read;
            return read;
        }
#endif

#if NET
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            SignalWhenDrained();
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _read += read;
            return read;
        }
#endif

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SignalWhenDrained()
        {
            if (_read >= bodyLength)
            {
                _ = stalled.TrySetResult(true);
            }
        }
    }
}
