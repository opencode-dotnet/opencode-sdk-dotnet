using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>A string-heavy one-shot diff, decomposed from the complete call to the raw-send floor.</summary>
[MemoryDiagnoser]
public class SessionDiffBenchmarks : IDisposable
{
    private const string SessionId = "ses_bench0000000000000000001";
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _envelope = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private CannedExchange? _raw;
    private SessionClient? _session;
    private Pipeline? _pipeline;

    public static IEnumerable<WireFixture> Fixtures()
    {
        // UTF-8 byte counts after JSON escaping and the nine-byte data envelope: 4,534 and 1,266,444.
        // The large body is 20.78% above the downlevel shared pool's 1,048,576-byte boundary.
        yield return Envelope("diff-4k", 36);
        yield return Envelope("diff-1200k", 10000);
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
        _raw = new CannedExchange(_httpClient, HttpMethod.Get, Options.Endpoint!);
        await _raw.RequireDrainAsync(_envelope.Length).ConfigureAwait(false);
        _session = _client.Sessions.GetSessionClient(SessionId);
        _pipeline = new Pipeline(_httpClient, ownsHttpClient: false, Options);

        var response = await GetDiffAsync().ConfigureAwait(false);
        RequireDiff(response);
        RequireDiff(AdaptSuccess());
        if (DeserializeEnvelope() is not SessionDiffResponseEnvelope { Data.Count: 1 })
        {
            throw new InvalidOperationException("The diff envelope did not materialize one file.");
        }

        _ = await ExecuteWithoutAdapterAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public Task<SessionDiffResponse> GetDiffAsync() => _session!.GetDiffAsync();

    [Benchmark]
    public Task<NoOpResponse> ExecuteWithoutAdapterAsync() =>
        _pipeline!.ExecuteAsync("session.diff", HttpMethod.Get, OpenCodeRoutes.Sessions.GetDiff(SessionId), NoOpResponseAdapter.Instance, options: null, CancellationToken.None);

    [Benchmark]
    public SessionDiffResponse AdaptSuccess() => SessionDiffResponseAdapter.Instance.AdaptSuccess(200, _envelope);

    [Benchmark]
    public object? DeserializeEnvelope() => JsonSerializer.Deserialize(_envelope, OpenCodeJsonContext.Default.SessionDiffResponseEnvelope);

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

        _pipeline?.Dispose();
        _client?.Dispose();
        _httpClient?.Dispose();
        _handler?.Dispose();
    }

    private static void RequireDiff(SessionDiffResponse response)
    {
        if (response.Diffs is not [{ File: "src/settings.ts", Patch.Length: > 4000, Additions: > 0, Deletions: > 0 }])
        {
            throw new InvalidOperationException("The diff fixture did not materialize one source patch.");
        }
    }

    private static WireFixture Envelope(string name, int hunks)
    {
        var payload = BenchmarkFixtures.SessionDiffBody(hunks);
        return new WireFixture(name, BenchmarkFixtures.DataEnvelope(payload), items: 1, payloadBytesPerItem: payload.Length);
    }
}
