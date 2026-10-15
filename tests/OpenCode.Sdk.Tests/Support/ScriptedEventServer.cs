using System.Net;
using System.Net.Http.Headers;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// An event bus that answers each subscribe from a script indexed by the request's number, on a
/// clock the test moves, behind the real <see cref="EventsClient"/>. It reports each request as it
/// arrives, and whether every response it gave before had been released by then, so a test sees how
/// many connections the client opened, when, and that each one was let go before the next.
/// </summary>
internal sealed class ScriptedEventServer : IDisposable
{
    public const string Connected = "{\"id\":\"evt_c\",\"type\":\"server.connected\",\"data\":{}}";

    public const string Heartbeat = ": heartbeat\n\n";

    private readonly Handler _handler;
    private readonly HttpClient _httpClient;
    private readonly Pipeline _pipeline;

    /// <param name="answer">Answers request number <c>n</c> (from 1); <see cref="Silence"/> never answers it.</param>
    /// <param name="networkTimeout">The client's network timeout, which the bus must not take.</param>
    public ScriptedEventServer(Func<int, HttpResponseMessage?> answer, TimeSpan? networkTimeout = null)
    {
        _handler = new Handler(answer);
        _httpClient = new HttpClient(_handler, disposeHandler: false);
        _pipeline = PipelineFactory.Create(_httpClient, networkTimeout: networkTimeout, time: Time);
        Events = new EventsClient(_pipeline);
    }

    /// <summary>Gets the answer that never comes: the headers wait until the client gives up on them.</summary>
    public static HttpResponseMessage? Silence => null;

    public ManualTimeProvider Time { get; } = new();

    public EventsClient Events { get; }

    /// <summary>Gets how many subscribes arrived so far.</summary>
    public int RequestCount => _handler.RequestCount;

    /// <summary>Gets the method and address of the last subscribe.</summary>
    public (HttpMethod Method, Uri? Address) LastRequest => _handler.LastRequest;

    /// <summary>Gets, for each subscribe, whether every response given before it had been released when it arrived.</summary>
    public IReadOnlyList<bool> EarlierResponsesReleased => _handler.EarlierResponsesReleased;

    /// <summary>Gets whether every response given so far has been released.</summary>
    public bool AllResponsesReleased => _handler.AllResponsesReleased;

    /// <summary>Answers with the given event-stream body, which the test keeps writing to.</summary>
    public static HttpResponseMessage Stream(LiveEventBody body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new TrackedResponse(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Answers with an event stream that holds the given text and stays open, or ends after it.</summary>
    public static HttpResponseMessage StreamOf(string written, bool ends = false)
    {
        var content = new StreamContent(new LiveEventBody(written, ends));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new TrackedResponse(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Answers with a status and a JSON body, before any stream opens.</summary>
    public static HttpResponseMessage Status(HttpStatusCode status, string body) =>
        new TrackedResponse(status) { Content = new StringContent(body) };

    /// <summary>Completes once subscribe number <paramref name="count"/> has arrived.</summary>
    public Task WaitForRequestAsync(int count, TimeSpan hangGuard) => _handler.WaitForRequestAsync(count, hangGuard);

    public void Dispose()
    {
        _pipeline.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>Disposes the client side, as a caller disposing its client does.</summary>
    public void DisposeClient() => _pipeline.Dispose();

    /// <summary>A response that reports whether the client released it.</summary>
    private sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        private int _released;

        public bool IsReleased => Volatile.Read(ref _released) is not 0;

        protected override void Dispose(bool disposing)
        {
            _ = Interlocked.Exchange(ref _released, 1);
            base.Dispose(disposing);
        }
    }

    private sealed class Handler(Func<int, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<(int Count, TaskCompletionSource Arrived)> _waiters = [];
        private readonly List<TrackedResponse> _responses = [];
        private readonly List<bool> _earlierReleased = [];
        private (HttpMethod Method, Uri? Address) _lastRequest = (HttpMethod.Get, null);
        private int _requests;

        public int RequestCount
        {
            get
            {
                lock (_gate)
                {
                    return _requests;
                }
            }
        }

        public (HttpMethod Method, Uri? Address) LastRequest
        {
            get
            {
                lock (_gate)
                {
                    return _lastRequest;
                }
            }
        }

        public IReadOnlyList<bool> EarlierResponsesReleased
        {
            get
            {
                lock (_gate)
                {
                    return [.. _earlierReleased];
                }
            }
        }

        public bool AllResponsesReleased
        {
            get
            {
                lock (_gate)
                {
                    return _responses.TrueForAll(static response => response.IsReleased);
                }
            }
        }

        public Task WaitForRequestAsync(int count, TimeSpan hangGuard)
        {
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_requests >= count)
                {
                    return Task.CompletedTask;
                }

                _waiters.Add((count, arrived));
            }

            return arrived.Task.WaitAsync(hangGuard);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int number;
            lock (_gate)
            {
                number = ++_requests;
                _lastRequest = (request.Method, request.RequestUri);
                _earlierReleased.Add(_responses.TrueForAll(static response => response.IsReleased));
                foreach (var waiter in _waiters.Where(waiter => waiter.Count <= number).ToArray())
                {
                    _ = _waiters.Remove(waiter);
                    _ = waiter.Arrived.TrySetResult();
                }
            }

            if (answer(number) is { } response)
            {
                lock (_gate)
                {
                    if (response is TrackedResponse tracked)
                    {
                        _responses.Add(tracked);
                    }
                }

                return Task.FromResult(response);
            }

            var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            return never.Task.WaitAsync(cancellationToken);
        }
    }
}
