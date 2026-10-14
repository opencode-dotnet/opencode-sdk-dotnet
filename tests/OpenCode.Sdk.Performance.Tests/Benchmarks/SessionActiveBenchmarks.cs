using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The dictionary-envelope shape <c>session.active</c> introduced — <c>{"data": {...}}</c> keyed
/// by session ID, rather than the ordered array a cursor-list page carries — decomposed per
/// entry count: the complete generated operation, the generated adapter over validated UTF-8,
/// and source-generated materialization of the envelope and its dictionary alone. Entry counts
/// span the common case to very large, mirroring <see cref="MessageListBenchmarks"/>'s size
/// idiom.
/// </summary>
/// <remarks>
/// The two other envelope shapes envelope completion produced deliberately get no rung here or
/// elsewhere: the Data-list shape (<c>{"data": [...]}</c> plus cursor) is already
/// component-laddered by <see cref="MessageListBenchmarks"/>, and the bare-container shape (no
/// envelope at all) is already component-laddered by <see cref="InfoBenchmarks"/>. Neither
/// materializes a dictionary, so neither stands in for this shape — this is the asymmetry's only
/// new rung, not an oversight.
/// </remarks>
[MemoryDiagnoser]
public class SessionActiveBenchmarks : IDisposable
{
    private const int MediumEntries = 16;
    private const int LargeEntries = 256;

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _body = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;

    public static IEnumerable<WireFixture> Fixtures()
    {
        yield return Entries("entries-1", 1);
        yield return Entries("entries-16", MediumEntries);
        yield return Entries("entries-256", LargeEntries);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _body = Fixture.Bytes;
        _handler = new CannedResponseHandler(_body);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);

        var response = await GetActiveAsync().ConfigureAwait(false);
        if (response.Active.Count != Fixture.Items)
        {
            throw new InvalidOperationException("The active-sessions fixture did not materialize the expected entry count.");
        }
    }

    /// <summary>The complete generated operation.</summary>
    [Benchmark]
    public Task<SessionActiveResponse> GetActiveAsync() => _client!.Sessions.GetActiveAsync();

    /// <summary>The generated adapter over validated UTF-8: dictionary materialization plus the response record.</summary>
    [Benchmark]
    public SessionActiveResponse AdaptSuccess() => SessionActiveResponseAdapter.Instance.AdaptSuccess(200, _body);

    /// <summary>Source-generated materialization of the envelope and its dictionary alone.</summary>
    [Benchmark]
    public object? DeserializeEnvelope() =>
        JsonSerializer.Deserialize(_body, OpenCodeJsonContext.Default.SessionActiveResponseEnvelope);

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

    private static WireFixture Entries(string name, int count)
    {
        var dictionary = BenchmarkFixtures.SessionActiveDictionary(count);
        var body = BenchmarkFixtures.DataEnvelope(dictionary);
        return new WireFixture(name, body, count, payloadBytesPerItem: dictionary.Length / count);
    }
}
