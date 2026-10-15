using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The cursor-list page read, decomposed per page size: the complete call, the generated
/// adapter over validated UTF-8, and source-generated envelope materialization alone. Pages
/// carry one, thirty, and four hundred eighty deep messages, so per-item and fixed costs separate.
/// The raw-send rung is the harness alone (<see cref="CannedExchange"/>), so a pipeline rung minus
/// it is the SDK's share.
/// </summary>
[MemoryDiagnoser]
public class MessageListBenchmarks : IDisposable
{
    private const int MediumItems = 30;
    private const int LargeItems = 480;

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _page = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private CannedExchange? _raw;
    private SessionClient? _session;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var composer = new AssistantMessageComposer(BenchmarkFixtures.DeepAssistantMessage());
        yield return Page("page-1", composer, 1);
        yield return Page("page-30", composer, MediumItems);
        yield return Page("page-480", composer, LargeItems);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _page = Fixture.Bytes;
        _handler = new CannedResponseHandler(_page);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        _session = _client.Sessions.GetSessionClient("ses_bench0000000000000000001");

        var response = await ListMessagesAsync().ConfigureAwait(false);
        if (response.Messages.Count != Fixture.Items || response.Cursor.Next is null)
        {
            throw new InvalidOperationException("The page fixture did not materialize the expected items and next cursor.");
        }
    }

    /// <summary>The complete generated page operation.</summary>
    [Benchmark]
    public Task<SessionMessageListResponse> ListMessagesAsync() => _session!.ListMessagesAsync();

    /// <summary>The generated adapter over validated UTF-8: page materialization plus the response record.</summary>
    [Benchmark]
    public SessionMessageListResponse AdaptSuccess() => SessionMessageListResponseAdapter.Instance.AdaptSuccess(200, _page);

    /// <summary>Source-generated materialization of the page envelope and its items alone.</summary>
    [Benchmark]
    public object? DeserializeEnvelope() =>
        JsonSerializer.Deserialize(_page, OpenCodeJsonContext.Default.SessionMessageListResponseEnvelope);

    /// <summary>The harness floor: the same canned handler through a bare <see cref="HttpClient"/> send with the body drained, no SDK.</summary>
    [Benchmark]
    public Task<int> SendRawRequestAsync() => _raw!.SendAndDrainAsync();

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

        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private static WireFixture Page(string name, AssistantMessageComposer composer, int items)
    {
        var payloads = composer.Page(items);
        return new WireFixture(name, BenchmarkFixtures.CursorListEnvelope(payloads), items, payloadBytesPerItem: payloads[0].Length);
    }
}
