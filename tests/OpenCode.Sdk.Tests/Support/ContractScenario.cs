using System.Net;
using System.Net.Http.Headers;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Assembles an <c>OpenCodeClient</c> over a recording transport answering with one canned response.</summary>
internal sealed class ContractScenario : IDisposable
{
    public static readonly Uri Endpoint = new("http://localhost:4096");

    private readonly RecordingHttpHandler _handler;
    private readonly HttpClient _httpClient;

    private ContractScenario(RecordingHttpHandler handler, string? password = null)
    {
        _handler = handler;
        _httpClient = new HttpClient(handler);
        Client = new OpenCodeClient(_httpClient, new OpenCodeClientOptions
        {
            Endpoint = Endpoint,
            Password = password,
        });
    }

    public OpenCodeClient Client { get; }

    public IReadOnlyList<CancellationToken> CancellationTokens => _handler.CancellationTokens;

    public IReadOnlyList<RecordedRequest> Requests => _handler.Requests;

    public static ContractScenario Responding(HttpStatusCode status, string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return new(new RecordingHttpHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        }));
    }

    public static ContractScenario Responding(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        return new(new RecordingHttpHandler(responder));
    }

    public static ContractScenario Responding() => new(new RecordingHttpHandler());

    /// <summary>The same canned response, reached by a client that carries a Basic password.</summary>
    public static ContractScenario RespondingToCredentialed(HttpStatusCode status, string body, string password)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return new(
            new RecordingHttpHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body), }),
            password);
    }

    /// <summary>Answers with a server-sent event body, the shape a streaming operation reads.</summary>
    public static ContractScenario RespondingWithFrames(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        return new(new RecordingHttpHandler(_ =>
        {
            var content = new StringContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, };
        }));
    }

    public void Dispose()
    {
        Client.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
    }
}
