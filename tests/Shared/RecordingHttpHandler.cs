using System.Net;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>Records every request flowing through the handler, answering from the supplied responder.</summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private readonly List<CancellationToken> _cancellationTokens = [];
    private readonly List<RecordedRequest> _requests = [];

    public RecordingHttpHandler()
        : this(static _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        })
    {
    }

    public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        _responder = responder;
    }

    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public IReadOnlyList<CancellationToken> CancellationTokens => _cancellationTokens;

    public bool IsDisposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _cancellationTokens.Add(cancellationToken);

        // The declared length is read before the body is buffered: once buffered, the content
        // reports the buffer's length instead of what it declared on its own.
        var contentLength = request.Content?.Headers.ContentLength;
        var bodyBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        _requests.Add(new RecordedRequest
        {
            RequestUri = request.RequestUri,
            Method = request.Method,
            Headers = request.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(',', header.Value),
                StringComparer.OrdinalIgnoreCase),
            Authorization = request.Headers.Authorization?.ToString(),
            UserAgent = request.Headers.UserAgent.Count is 0 ? null : request.Headers.UserAgent.ToString(),
            ContentType = request.Content?.Headers.ContentType?.ToString(),
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            BodyBytes = bodyBytes,
            ContentLength = contentLength,
        });
        return _responder(request);
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
