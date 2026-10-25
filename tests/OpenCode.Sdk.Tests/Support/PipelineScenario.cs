using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Owns a canned-response transport for the full lifetime of a pipeline test.</summary>
internal sealed class PipelineScenario : IDisposable
{
    private readonly RecordingHttpHandler _handler;
    private readonly HttpClient _httpClient;

    private PipelineScenario(Func<HttpRequestMessage, HttpResponseMessage>? responder, Func<HttpClient, Pipeline> createPipeline)
    {
        _handler = responder is null ? new RecordingHttpHandler() : new RecordingHttpHandler(responder);
        HttpClient? httpClient = null;
        try
        {
            httpClient = new HttpClient(_handler);
            Pipeline = createPipeline(httpClient);
            _httpClient = httpClient;
        }
        catch
        {
            httpClient?.Dispose();
            _handler.Dispose();
            throw;
        }
    }

    public Pipeline Pipeline { get; }

    public HttpClient HttpClient => _httpClient;

    public IReadOnlyList<RecordedRequest> Requests => _handler.Requests;

    public IReadOnlyList<CancellationToken> CancellationTokens => _handler.CancellationTokens;

    public bool IsDisposed => _handler.IsDisposed;

    public static PipelineScenario Responding(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        TimeSpan? networkTimeout = null,
        TimeProvider? time = null) => new(responder,
        httpClient => PipelineFactory.Create(httpClient, ownsHttpClient: false, networkTimeout: networkTimeout, time: time));

    /// <summary>Creates a scenario whose pipeline decorates its requests with the server password and the client location.</summary>
    public static PipelineScenario Decorating(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        string? password = null,
        LocationSelector? location = null) => new(responder,
        httpClient => PipelineFactory.Create(httpClient, ownsHttpClient: false, password: password, location: location));

    public void Dispose()
    {
        Pipeline.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
    }
}
