using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Owns a canned-response transport for the full lifetime of a pipeline test.</summary>
internal sealed class PipelineScenario : IDisposable
{
    private readonly RecordingHttpHandler _handler;
    private readonly HttpClient _httpClient;

    private PipelineScenario(Func<HttpRequestMessage, HttpResponseMessage>? responder, TimeSpan? networkTimeout, TimeProvider? time)
    {
        _handler = responder is null ? new RecordingHttpHandler() : new RecordingHttpHandler(responder);
        HttpClient? httpClient = null;
        try
        {
            httpClient = new HttpClient(_handler);
            Pipeline = PipelineFactory.Create(httpClient, ownsHttpClient: false, networkTimeout: networkTimeout, time: time);
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

    public IReadOnlyList<RecordedRequest> Requests => _handler.Requests;

    public bool IsDisposed => _handler.IsDisposed;

    public static PipelineScenario Responding(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null,
        TimeSpan? networkTimeout = null,
        TimeProvider? time = null) => new(responder, networkTimeout, time);

    public void Dispose()
    {
        Pipeline.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
    }
}
