using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The smallest one-shot operation, decomposed so the fixed pipeline cost can be read off:
/// the complete call, the same pipeline through an adapter that materializes nothing, the
/// generated adapter alone, and source-generated materialization alone. Each row is the one
/// above it minus one layer.
/// </summary>
[MemoryDiagnoser]
public class InfoBenchmarks : IDisposable
{
    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private byte[] _body = [];
    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;
    private Pipeline? _pipeline;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var body = BenchmarkFixtures.InfoBody();
        yield return new WireFixture("info", body, items: 1, payloadBytesPerItem: body.Length);
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
        _pipeline = new Pipeline(_httpClient, ownsHttpClient: false, Options);

        var response = await GetInfoAsync().ConfigureAwait(false);
        if (response.ServerInfo is not { Version: "0.0.0-bench", Pid: 42 })
        {
            throw new InvalidOperationException("The info fixture did not materialize the expected server identity.");
        }
    }

    /// <summary>The complete generated operation: request, decoration, send, buffer, validate, adapt, materialize.</summary>
    [Benchmark]
    public Task<ServerInfoResponse> GetInfoAsync() => _client!.Server.GetInfoAsync();

    /// <summary>The same pipeline through a no-op adapter: everything above minus JSON and model cost.</summary>
    [Benchmark]
    public Task<NoOpResponse> ExecuteWithoutAdapterAsync() =>
        _pipeline!.ExecuteAsync(HttpMethod.Get, OpenCodeRoutes.Server.GetInfo, NoOpResponseAdapter.Instance, options: null, CancellationToken.None);

    /// <summary>The generated adapter over validated UTF-8: materialization plus the response envelope.</summary>
    [Benchmark]
    public ServerInfoResponse AdaptSuccess() => ServerInfoResponseAdapter.Instance.AdaptSuccess(200, _body);

    /// <summary>Source-generated materialization alone.</summary>
    [Benchmark]
    public ServerInfo? Deserialize() => JsonSerializer.Deserialize(_body, OpenCodeJsonContext.Default.ServerInfo);

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
}
