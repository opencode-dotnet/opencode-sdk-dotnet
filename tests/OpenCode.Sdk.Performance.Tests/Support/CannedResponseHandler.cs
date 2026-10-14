using System.Net;
using System.Net.Http.Headers;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// Answers every request with the same status, body, and media type, so benchmarks measure the SDK
/// pipeline and serialization only — no network, no server. The per-call response/content
/// allocations cancel only between two rungs that both send, such as the complete-operation and
/// no-adapter pipeline rungs; a rung that does not send carries none of them.
/// </summary>
internal sealed class CannedResponseHandler : HttpMessageHandler
{
    private readonly byte[]? _body;
    private readonly MediaTypeHeaderValue _mediaType;
    private readonly HttpStatusCode _status;

    public CannedResponseHandler(byte[] body, string mediaType = "application/json", string? charset = null,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        _body = body;
        _mediaType = MediaTypeHeaderValue.Parse(charset is null ? mediaType : $"{mediaType}; charset={charset}");
        _status = status;
    }

    private CannedResponseHandler(HttpStatusCode status)
    {
        _mediaType = MediaTypeHeaderValue.Parse("application/json");
        _status = status;
    }

    /// <summary>A handler answering a declared no-content success with no body at all.</summary>
    public static CannedResponseHandler NoContent() => new(HttpStatusCode.NoContent);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status);
        if (_body is not null)
        {
            var content = new ByteArrayContent(_body);
            // Clone the parsed template because each response owns its mutable ContentType.
            content.Headers.ContentType = (MediaTypeHeaderValue)((ICloneable)_mediaType).Clone();
            response.Content = content;
        }

        return Task.FromResult(response);
    }
}
