using System.Text.Json;
using BenchmarkDotNet.Attributes;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Performance.Tests.Support;

namespace OpenCode.Sdk.Performance.Tests.Benchmarks;

/// <summary>
/// The first body-carrying operation: a POST whose typed request serializes onto the wire and
/// whose envelope materializes the created session. The serialization row isolates the request
/// side, which no response-only benchmark can see.
/// </summary>
[MemoryDiagnoser]
public class SessionCreateBenchmarks : IDisposable
{
    private static readonly SessionCreateRequest Request = new()
    {
        Title = "Fix the build",
    };

    private static readonly OpenCodeClientOptions Options = new()
    {
        Endpoint = new Uri("https://benchmark.invalid"),
        Password = "benchmark",
    };

    private CannedResponseHandler? _handler;
    private HttpClient? _httpClient;
    private OpenCodeClient? _client;

    public static IEnumerable<WireFixture> Fixtures()
    {
        var payload = BenchmarkFixtures.SessionInfoBody();
        yield return new WireFixture("session-envelope", BenchmarkFixtures.DataEnvelope(payload), items: 1, payloadBytesPerItem: payload.Length);
    }

    [ParamsSource(nameof(Fixtures))]
    public WireFixture Fixture { get; set; } = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _handler = new CannedResponseHandler(Fixture.Bytes);
        _httpClient = new HttpClient(_handler);
        _client = new OpenCodeClient(_httpClient, Options);

        var response = await CreateSessionAsync().ConfigureAwait(false);
        if (response.Session is not { Id: "ses_bench0000000000000000001" })
        {
            throw new InvalidOperationException("The session fixture did not materialize the created session.");
        }
    }

    /// <summary>The complete generated operation: request serialization, send, and envelope materialization.</summary>
    [Benchmark]
    public Task<SessionCreateResponse> CreateSessionAsync() => _client!.Sessions.CreateSessionAsync(Request);

    /// <summary>The same operation with the body omitted: the generated empty-request path.</summary>
    [Benchmark]
    public Task<SessionCreateResponse> CreateSessionOmittedBodyAsync() => _client!.Sessions.CreateSessionAsync();

    /// <summary>Request-body serialization alone, exactly as the pipeline produces the JSON content bytes.</summary>
    [Benchmark]
    public byte[] SerializeRequest() => JsonSerializer.SerializeToUtf8Bytes(Request, OpenCodeJsonContext.Default.SessionCreateRequest);

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
}
