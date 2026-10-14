using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The single-message read, decomposed per wire size: the complete call, the same pipeline
/// through a no-op adapter (request, send, buffer, validate), the generated adapter over
/// validated UTF-8, and source-generated envelope materialization alone. The three fixtures
/// scale the same message from the wire's common size to a very large one, so per-byte and
/// fixed costs separate.
/// </summary>
[MemoryDiagnoser]
public class MessageGetBenchmarks : IDisposable
{
    private const int MediumParts = 120;
    private const int LargeParts = 2400;

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _envelope = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private SessionClient? _session;
    private Pipeline? _pipeline;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var composer = new AssistantMessageComposer(BenchmarkFixtures.DeepAssistantMessage());
        yield return Envelope("deep-message", composer.MarkerEarly());
        yield return Envelope("medium-message", composer.WithContentParts(MediumParts));
        yield return Envelope("large-message", composer.WithContentParts(LargeParts));
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _envelope = Fixture.Bytes;
        _handler = new CannedResponseHandler(_envelope);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _session = _client.Sessions.GetSessionClient("ses_bench0000000000000000001");
        _pipeline = new Pipeline(_httpClient, ownsHttpClient: false, Options);

        var response = await GetMessageAsync().ConfigureAwait(false);
        if (response.Message is not SessionMessageAssistant { Content.Count: > 0 })
        {
            throw new InvalidOperationException("The message fixture did not materialize as an assistant message with content.");
        }
    }

    /// <summary>The complete generated operation.</summary>
    [Benchmark]
    public Task<SessionMessageResponse> GetMessageAsync() => _session!.GetMessageAsync("msg_bench00000000000000000001");

    /// <summary>The same pipeline through a no-op adapter: request, send, buffer, and UTF-8 validation only.</summary>
    [Benchmark]
    public Task<NoOpResponse> ExecuteWithoutAdapterAsync() =>
        _pipeline!.ExecuteAsync(HttpMethod.Get, OpenCodeRoutes.Server.GetInfo, NoOpResponseAdapter.Instance, options: null, CancellationToken.None);

    /// <summary>The generated adapter over validated UTF-8: envelope materialization plus the response record.</summary>
    [Benchmark]
    public SessionMessageResponse AdaptSuccess() => SessionMessageResponseAdapter.Instance.AdaptSuccess(200, _envelope);

    /// <summary>Source-generated materialization of the envelope and its nested unions alone.</summary>
    [Benchmark]
    public object? DeserializeEnvelope() =>
        JsonSerializer.Deserialize(_envelope, OpenCodeJsonContext.Default.SessionMessageResponseEnvelope);

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _pipeline?.Dispose();
        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private static WireFixture Envelope(string name, byte[] payload) =>
        new(name, BenchmarkFixtures.DataEnvelope(payload), items: 1, payloadBytesPerItem: payload.Length);
}
